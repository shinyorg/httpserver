using System.Buffers;
using System.IO.Pipelines;

namespace Shiny.Net.HttpServer.Http2;

/// <summary>Stream lifecycle (RFC 9113 §5.1), reduced to what a server needs.</summary>
enum Http2StreamState
{
    Idle,
    Open,
    /// <summary>The client has finished sending; we may still be responding.</summary>
    HalfClosedRemote,
    Closed
}

/// <summary>
/// One request/response exchange on an HTTP/2 connection.
/// <para>
/// The request body arrives as DATA frames from the connection's single read loop and is handed to
/// the handler through a pipe, so a handler can read its body while other streams are being served.
/// Flow control is per stream <em>and</em> per connection: a stream may have window to spare while
/// the connection has none, and sending anyway is a protocol violation the peer will kill the
/// connection over.
/// </para>
/// <para>
/// The pipe never pushes back on the read loop. It is the receive window that bounds what can pile
/// up in it: the client may only send what we have credited, and credit goes back as the handler
/// reads, not as bytes arrive. A pipe that paused instead would stop the one loop every stream on
/// the connection shares, the moment any handler was slow to read.
/// </para>
/// </summary>
sealed class Http2Stream
{
    readonly Pipe requestBody;
    readonly object bodyGate = new();
    long sendWindow;
    long receiveWindow;
    long pendingCredit;
    long unread;
    bool readerDone;
    bool writerDone;
    int state = (int)Http2StreamState.Idle;

    public Http2Stream(uint id, int initialSendWindow, int initialReceiveWindow)
    {
        this.Id = id;
        this.sendWindow = initialSendWindow;
        this.receiveWindow = initialReceiveWindow;

        // Never paused - see the class remarks. The receive window is the bound.
        this.requestBody = new Pipe(new PipeOptions(
            pauseWriterThreshold: 0,
            resumeWriterThreshold: 0,
            useSynchronizationContext: false
        ));
    }

    public uint Id { get; }

    /// <summary>
    /// Read by the response writer and the handler while the read loop moves it. Closed is final: the read loop can
    /// decide to half-close a stream a handler is resetting at that moment, and a plain write would reopen it - and
    /// with it the guards that keep frames from following our own RST_STREAM.
    /// </summary>
    public Http2StreamState State
    {
        get => (Http2StreamState)Volatile.Read(ref this.state);
        set
        {
            int current;
            do
            {
                current = Volatile.Read(ref this.state);
                if (current == (int)Http2StreamState.Closed)
                    return;
            }
            while (Interlocked.CompareExchange(ref this.state, (int)value, current) != current);
        }
    }

    /// <summary>How many body bytes we may still send. Adjusted by WINDOW_UPDATE from the peer.</summary>
    public long SendWindow => Interlocked.Read(ref this.sendWindow);

    /// <summary>
    /// Moves <see cref="SendWindow"/> by <paramref name="delta"/> and returns the new value. Atomic
    /// because two threads move it at once: the response writer spends it while the connection's
    /// read loop refills it from WINDOW_UPDATE. A plain <c>+=</c> could lose a refill to a
    /// concurrent spend, and with it the only credit the peer would ever send - the response then
    /// waits for a window that never opens.
    /// </summary>
    public long AdjustSendWindow(long delta) => Interlocked.Add(ref this.sendWindow, delta);

    /// <summary>
    /// How many body bytes the peer may still send us. Spent by the read loop as DATA arrives and
    /// refilled from the handler's thread as it reads, so it is atomic like the send window.
    /// </summary>
    public long ReceiveWindow => Interlocked.Read(ref this.receiveWindow);

    public long AdjustReceiveWindow(long delta) => Interlocked.Add(ref this.receiveWindow, delta);

    /// <summary>
    /// Adds bytes the handler has read to the credit waiting to go back to the peer, returning the
    /// total that is waiting. The caller decides when it is worth a WINDOW_UPDATE.
    /// </summary>
    public long AddPendingCredit(long consumed) => Interlocked.Add(ref this.pendingCredit, consumed);

    /// <summary>Claims all of the waiting credit, so only one caller sends it.</summary>
    public long TakePendingCredit() => Interlocked.Exchange(ref this.pendingCredit, 0);

    /// <summary>Signalled when <see cref="SendWindow"/> grows or the stream ends, so a blocked writer can retry.</summary>
    public Http2Signal WindowAvailable { get; } = new();

    public PipeReader RequestBodyReader => this.requestBody.Reader;

    /// <summary>True once the handler has been dispatched, so a duplicate HEADERS is an error.</summary>
    public bool Dispatched { get; set; }

    /// <summary>
    /// Whether the HEADERS that opened the stream left it open for DATA — decided at that frame, since
    /// by the time the handler runs the body may already have arrived and ended the stream.
    /// </summary>
    public bool HasRequestBody { get; set; }

    /// <summary>The request's Content-Length, which the DATA frames have to add up to (RFC 9113 §8.1.1).</summary>
    public long? DeclaredContentLength { get; set; }

    /// <summary>Body bytes received so far, padding excluded. Only the read loop touches it.</summary>
    public long ReceivedBodyBytes { get; set; }

    /// <summary>Called on the handler's thread with each count of body bytes it reads.</summary>
    public Func<int, ValueTask>? BodyConsumed { get; set; }

    /// <summary>
    /// How long one read of the request body may wait for the client to send more of it -
    /// <see cref="HttpServerLimits.RequestBodyTimeout"/>, as over HTTP/1.1.
    /// </summary>
    public TimeSpan BodyReadTimeout { get; set; } = Timeout.InfiniteTimeSpan;

    public Task? Handler { get; set; }

    /// <summary>Cancelled when the peer resets the stream or the connection goes away.</summary>
    public CancellationTokenSource Aborted { get; } = new();

    /// <summary>
    /// Hands received body bytes to the handler. Returns false when nobody will ever read them -
    /// the handler has finished - in which case the caller owns their credit and must give it back.
    /// </summary>
    public bool TryWriteBody(ReadOnlySequence<byte> body)
    {
        // The read loop writes here while a handler thread may be resetting the stream, which completes the
        // writer: both happen under the gate, and a write after completion is turned away rather than thrown.
        lock (this.bodyGate)
        {
            if (this.readerDone || this.writerDone)
                return false;

            foreach (var segment in body)
                this.requestBody.Writer.Write(segment.Span);

            this.unread += body.Length;

            // The pipe never pauses, so this completes synchronously; all it does is wake the reader.
            this.requestBody.Writer.FlushAsync().GetAwaiter().GetResult();
        }

        return true;
    }

    /// <summary>The handler read <paramref name="count"/> bytes; they no longer sit in the pipe.</summary>
    public void MarkRead(int count)
    {
        lock (this.bodyGate)
            this.unread -= count;
    }

    /// <summary>
    /// The handler is done with the body. Returns the bytes still buffered and never read, whose
    /// connection credit the caller has to give back - nothing else will.
    /// </summary>
    public long CompleteReader()
    {
        long leftover;

        lock (this.bodyGate)
        {
            this.readerDone = true;
            leftover = this.unread;
            this.unread = 0;
        }

        this.requestBody.Reader.Complete();
        return leftover;
    }

    public void CompleteRequestBody(Exception? error = null)
    {
        lock (this.bodyGate)
        {
            if (this.writerDone)
                return;

            this.writerDone = true;
            this.requestBody.Writer.Complete(error);
        }
    }

    public void Abort(Exception? reason = null)
    {
        // Closed first, so the read loop stops handing this stream frames as soon as possible.
        this.State = Http2StreamState.Closed;
        HttpContext.CancelQuietly(this.Aborted);
        this.CompleteRequestBody(reason);

        // Wake anything parked on the send window, so a writer notices the reset rather than
        // waiting for credit that is never coming.
        this.WindowAvailable.Set();
    }

    public void Dispose()
    {
        this.WindowAvailable.Set();
        this.Aborted.Dispose();
    }
}

/// <summary>
/// Wakes every waiter at once. A semaphore released once per WINDOW_UPDATE wakes one writer and
/// leaves the rest parked beside a window that has room for them; a fresh task per signal cannot.
/// A waiter takes <see cref="Task"/> before checking its condition, so a signal that lands in
/// between still completes the task it is about to await.
/// </summary>
sealed class Http2Signal
{
    TaskCompletionSource current = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Task => Volatile.Read(ref this.current).Task;

    public void Set()
    {
        var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref this.current, next).TrySetResult();
    }
}

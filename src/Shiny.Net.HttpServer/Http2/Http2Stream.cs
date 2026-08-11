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
/// </summary>
sealed class Http2Stream
{
    readonly Pipe requestBody;

    public Http2Stream(uint id, int initialSendWindow, int initialReceiveWindow)
    {
        this.Id = id;
        this.SendWindow = initialSendWindow;
        this.ReceiveWindow = initialReceiveWindow;
        this.requestBody = new Pipe(new PipeOptions(useSynchronizationContext: false));
    }

    public uint Id { get; }

    public Http2StreamState State { get; set; } = Http2StreamState.Idle;

    /// <summary>How many body bytes we may still send. Adjusted by WINDOW_UPDATE from the peer.</summary>
    public long SendWindow { get; set; }

    /// <summary>How many body bytes the peer may still send us before we top it up.</summary>
    public long ReceiveWindow { get; set; }

    /// <summary>Signalled when <see cref="SendWindow"/> grows, so a blocked writer can retry.</summary>
    public SemaphoreSlim WindowAvailable { get; } = new(0);

    public PipeReader RequestBodyReader => this.requestBody.Reader;

    public PipeWriter RequestBodyWriter => this.requestBody.Writer;

    /// <summary>True once the handler has been dispatched, so a duplicate HEADERS is an error.</summary>
    public bool Dispatched { get; set; }

    public Task? Handler { get; set; }

    /// <summary>Cancelled when the peer resets the stream or the connection goes away.</summary>
    public CancellationTokenSource Aborted { get; } = new();

    public void CompleteRequestBody() => this.requestBody.Writer.Complete();

    public void Abort(Exception? reason = null)
    {
        try
        {
            this.Aborted.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        this.requestBody.Writer.Complete(reason);
        this.State = Http2StreamState.Closed;

        // Release anything parked on the send window, so a writer notices the reset rather than
        // waiting for credit that is never coming.
        try
        {
            this.WindowAvailable.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public void Dispose()
    {
        this.Aborted.Dispose();
        this.WindowAvailable.Dispose();
    }
}

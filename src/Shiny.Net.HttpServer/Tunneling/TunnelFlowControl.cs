using System.Buffers;
using System.Buffers.Binary;
using System.Threading.Channels;

namespace Shiny.Net.HttpServer.Tunneling;

/// <summary>
/// What one end of a tunnel declares in its <see cref="TunnelFrameType.Settings"/> frame.
/// <para>
/// On the wire: a run of 6-byte entries, each a 2-byte big-endian id and a 4-byte big-endian value. An id a reader
/// does not know is skipped, so later versions can add entries without breaking earlier readers.
/// </para>
/// </summary>
/// <param name="Version">The flow-control version spoken. 1 today.</param>
/// <param name="InitialWindow">
/// How many bytes the sender may put on a stream before this end grants more with a
/// <see cref="TunnelFrameType.WindowUpdate"/> - per stream, per direction. It is what this end is prepared to buffer
/// for one stream whose destination has stopped taking data.
/// </param>
/// <param name="MaxStreams">How many flow-controlled streams this end will carry at once.</param>
public sealed record TunnelSettings(int Version, int InitialWindow, int MaxStreams)
{
    /// <summary>The flow-control version this implementation speaks.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The smallest window either end may declare: one full frame, so a sender can always make progress.</summary>
    public const int MinimumWindow = TunnelProtocol.MaxPayloadLength;

    /// <summary>The largest window either end may declare.</summary>
    public const int MaximumWindow = 1 << 30;

    const ushort VersionId = 1;
    const ushort InitialWindowId = 2;
    const ushort MaxStreamsId = 3;
    const int EntryLength = 6;

    /// <summary>Encodes the entries for a Settings frame.</summary>
    public byte[] Encode()
    {
        var payload = new byte[EntryLength * 3];
        WriteEntry(payload.AsSpan(0), VersionId, this.Version);
        WriteEntry(payload.AsSpan(EntryLength), InitialWindowId, this.InitialWindow);
        WriteEntry(payload.AsSpan(EntryLength * 2), MaxStreamsId, this.MaxStreams);
        return payload;
    }

    /// <summary>
    /// Reads a Settings frame's payload. Unknown entries are skipped; a payload that is not a whole number of entries,
    /// or a version, window or stream count out of range, is a protocol error.
    /// </summary>
    public static TunnelSettings Decode(ReadOnlySequence<byte> payload)
    {
        if (payload.Length % EntryLength != 0)
            throw new TunnelProtocolException($"A Settings payload must be a whole number of {EntryLength}-byte entries.");

        Span<byte> entry = stackalloc byte[EntryLength];
        int? version = null;
        int? window = null;
        int? streams = null;

        var remaining = payload;
        while (remaining.Length > 0)
        {
            remaining.Slice(0, EntryLength).CopyTo(entry);
            remaining = remaining.Slice(EntryLength);

            var id = BinaryPrimitives.ReadUInt16BigEndian(entry);
            var value = BinaryPrimitives.ReadUInt32BigEndian(entry[2..]);
            var clamped = (int)Math.Min(value, int.MaxValue);

            if (id == VersionId)
                version = clamped;
            else if (id == InitialWindowId)
                window = clamped;
            else if (id == MaxStreamsId)
                streams = clamped;
        }

        var settings = new TunnelSettings(
            version ?? throw new TunnelProtocolException("A Settings frame must declare its version."),
            window ?? throw new TunnelProtocolException("A Settings frame must declare its initial window."),
            streams ?? throw new TunnelProtocolException("A Settings frame must declare its stream limit.")
        );

        settings.Validate();
        return settings;
    }

    /// <summary>Throws <see cref="TunnelProtocolException"/> when a value is out of range.</summary>
    public void Validate()
    {
        if (this.Version < 1)
            throw new TunnelProtocolException($"Flow-control version {this.Version} is not valid.");

        if (this.InitialWindow is < MinimumWindow or > MaximumWindow)
            throw new TunnelProtocolException($"An initial window must be between {MinimumWindow} and {MaximumWindow} bytes, not {this.InitialWindow}.");

        if (this.MaxStreams < 1)
            throw new TunnelProtocolException($"A stream limit must be at least 1, not {this.MaxStreams}.");
    }

    static void WriteEntry(Span<byte> destination, ushort id, int value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, id);
        BinaryPrimitives.WriteUInt32BigEndian(destination[2..], (uint)value);
    }

    /// <summary>Encodes a <see cref="TunnelFrameType.WindowUpdate"/> payload.</summary>
    public static byte[] EncodeWindowUpdate(int increment)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)increment);
        return payload;
    }

    /// <summary>Reads a <see cref="TunnelFrameType.WindowUpdate"/> payload; null when it is malformed or zero.</summary>
    public static int? DecodeWindowUpdate(ReadOnlySequence<byte> payload)
    {
        if (payload.Length != 4)
            return null;

        Span<byte> bytes = stackalloc byte[4];
        payload.CopyTo(bytes);

        var increment = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        return increment is 0 or > int.MaxValue ? null : (int)increment;
    }
}

/// <summary>
/// How much one stream may still send to its peer: granted by the peer's Settings and refilled by its WindowUpdates.
/// <para>
/// Waited on by the one task that sends for the stream - the relay's request loop for a body, the client's response
/// pump - never by a frame loop, so a stream with no credit waits alone.
/// </para>
/// </summary>
sealed class StreamSendWindow(int initial)
{
    readonly Lock gate = new();
    long available = initial;
    bool closed;
    TaskCompletionSource? waiter;

    /// <summary>Bytes that may be sent right now.</summary>
    public long Available
    {
        get
        {
            lock (this.gate)
                return this.available;
        }
    }

    /// <summary>
    /// Takes up to <paramref name="wanted"/> bytes of credit, waiting while there is none. Returns 0 once the stream is
    /// closed - the caller stops sending.
    /// </summary>
    public async ValueTask<int> ReserveAsync(int wanted, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (this.gate)
            {
                if (this.closed)
                    return 0;

                if (this.available > 0)
                {
                    var granted = (int)Math.Min(this.available, wanted);
                    this.available -= granted;
                    return granted;
                }

                this.waiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = this.waiter.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Adds credit the peer granted. False when it would overflow the window - a peer bug.</summary>
    public bool Add(int increment)
    {
        lock (this.gate)
        {
            if (this.closed)
                return true;

            if (this.available + increment > int.MaxValue)
                return false;

            this.available += increment;
            this.Wake();
        }

        return true;
    }

    /// <summary>The stream is over: a waiting sender wakes and gets 0, and so does every later one.</summary>
    public void Close()
    {
        lock (this.gate)
        {
            this.closed = true;
            this.Wake();
        }
    }

    void Wake()
    {
        this.waiter?.TrySetResult();
        this.waiter = null;
    }
}

/// <summary>
/// The receiving half of one flow-controlled stream: frames are queued as they arrive and delivered by a pump of the
/// stream's own, which grants the sender more credit as the destination takes the bytes.
/// <para>
/// This is what keeps a slow destination from stopping the tunnel. Without it the frame loop delivered each frame
/// itself and waited for the destination - a public client that stopped reading, a handler that never read its upload
/// - and every other stream waited behind it. Here the loop only queues, and the sender's window bounds the queue.
/// </para>
/// </summary>
sealed class StreamReceiver
{
    readonly int window;
    readonly int creditThreshold;
    readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> deliver;
    readonly Func<int, ValueTask> grant;
    readonly Channel<byte[]> queue = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );
    readonly CancellationTokenSource aborted = new();
    readonly Task pump;

    // Bytes the sender has put on the stream that have not been granted back yet. The sender may never have more than
    // the window outstanding; more is a protocol violation.
    long outstanding;
    int stopped;

    /// <param name="window">The window this end declared, which the sender is held to.</param>
    /// <param name="deliver">
    /// Hands one chunk to the destination and completes once it has taken it. Returns false when the destination is
    /// gone for good; the rest is then discarded, and still granted, so the sender is never left waiting on it.
    /// </param>
    /// <param name="grant">Sends a WindowUpdate for that many bytes.</param>
    public StreamReceiver(
        int window,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> deliver,
        Func<int, ValueTask> grant
    )
    {
        this.window = window;
        this.deliver = deliver;
        this.grant = grant;

        // In batches, as HTTP/2 does: a WindowUpdate per frame doubles the frame count for nothing. Half a window keeps
        // the sender from running dry while a batch is in flight.
        this.creditThreshold = Math.Max(1, window / 2);
        this.pump = Task.Run(this.PumpAsync, CancellationToken.None);
    }

    /// <summary>Completes once everything queued has been delivered or discarded, after <see cref="Complete"/> or <see cref="Abort"/>.</summary>
    public Task Completion => this.pump;

    /// <summary>Bytes received and not yet granted back.</summary>
    public long Outstanding => Interlocked.Read(ref this.outstanding);

    /// <summary>
    /// Queues one frame's payload, copied - the frame's memory belongs to the reader. False when it takes the sender
    /// past its window: the peer is not honouring flow control, and the caller resets the stream.
    /// </summary>
    public bool TryEnqueue(ReadOnlySequence<byte> payload)
    {
        if (payload.IsEmpty)
            return true;

        var length = payload.Length;
        if (Interlocked.Add(ref this.outstanding, length) > this.window)
            return false;

        // A stream already over takes no more, but the bytes still count as received so the arithmetic stays honest.
        if (Volatile.Read(ref this.stopped) != 0)
            return true;

        this.queue.Writer.TryWrite(payload.ToArray());
        return true;
    }

    /// <summary>No more data is coming: deliver what is queued, then complete.</summary>
    public void Complete() => this.queue.Writer.TryComplete();

    /// <summary>The stream is over: what is queued is dropped and a delivery in progress is cancelled.</summary>
    public void Abort()
    {
        if (Interlocked.Exchange(ref this.stopped, 1) != 0)
            return;

        this.queue.Writer.TryComplete();
        HttpContext.CancelQuietly(this.aborted);
    }

    async Task PumpAsync()
    {
        var pending = 0;
        var discarding = false;

        try
        {
            await foreach (var chunk in this.queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (!discarding && Volatile.Read(ref this.stopped) == 0)
                    discarding = !await this.deliver(chunk, this.aborted.Token).ConfigureAwait(false);

                pending += chunk.Length;
                if (pending >= this.creditThreshold && Volatile.Read(ref this.stopped) == 0)
                {
                    Interlocked.Add(ref this.outstanding, -pending);
                    await this.grant(pending).ConfigureAwait(false);
                    pending = 0;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            // Aborted, or the destination or the tunnel went away mid-delivery: the stream is over either way.
            this.Abort();
        }
        finally
        {
            this.aborted.Dispose();
        }
    }
}

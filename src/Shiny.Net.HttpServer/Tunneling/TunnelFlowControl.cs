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
/// The tunnel's round trip, measured with the Ping and Pong frames both ends already answer. A stream's window only
/// needs to grow when it is the bottleneck - when the sender can fill it faster than a round trip - and telling that
/// takes knowing how long a round trip is.
/// </summary>
sealed class TunnelRoundTrip
{
    static readonly long Resample = System.Diagnostics.Stopwatch.Frequency * 5;
    static readonly long GiveUp = System.Diagnostics.Stopwatch.Frequency * 30;

    readonly Lock gate = new();
    long estimate;
    long pingSent;
    long lastSample;

    /// <summary>The smoothed round trip, or null before the first sample.</summary>
    public TimeSpan? Estimate
    {
        get
        {
            lock (this.gate)
                return this.estimate == 0 ? null : TimeSpan.FromSeconds((double)this.estimate / System.Diagnostics.Stopwatch.Frequency);
        }
    }

    /// <summary>
    /// True when a Ping should go out now to take a sample: none is in flight (or the last one was never answered) and
    /// the estimate is missing or more than a few seconds old. The caller sends the Ping.
    /// </summary>
    public bool TryBeginSample()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();

        lock (this.gate)
        {
            var inFlight = this.pingSent != 0 && now - this.pingSent < GiveUp;
            var fresh = this.lastSample != 0 && now - this.lastSample < Resample;

            if (inFlight || fresh)
                return false;

            this.pingSent = now;
            return true;
        }
    }

    /// <summary>Notes that a Ping went out for another reason - a keep-alive - so its Pong can be timed too.</summary>
    public void OnPingSent()
    {
        lock (this.gate)
        {
            if (this.pingSent == 0)
                this.pingSent = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    /// <summary>A Pong arrived: the time since the Ping is a sample.</summary>
    public void OnPong()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();

        lock (this.gate)
        {
            if (this.pingSent == 0)
                return;

            var sample = Math.Max(1, now - this.pingSent);
            this.pingSent = 0;
            this.lastSample = now;

            // Smoothed, as TCP does, so one delayed Pong behind a burst of data does not swing every window at once.
            this.estimate = this.estimate == 0 ? sample : (this.estimate * 7 + sample) / 8;
        }
    }
}

/// <summary>
/// How much room a tunnel has for windows to grow beyond the size they start at. Shared by every stream on one end of
/// the tunnel, so growth for throughput can never add up to more memory than this.
/// </summary>
sealed class StreamWindowBudget(long capacity)
{
    long remaining = capacity;

    /// <summary>Room left, for diagnostics and tests.</summary>
    public long Remaining => Interlocked.Read(ref this.remaining);

    /// <summary>Takes as much of <paramref name="wanted"/> as there is room for; returns what was taken.</summary>
    public long TakeUpTo(long wanted)
    {
        while (true)
        {
            var current = Interlocked.Read(ref this.remaining);
            var taken = Math.Min(current, wanted);
            if (taken <= 0)
                return 0;

            if (Interlocked.CompareExchange(ref this.remaining, current - taken, current) == current)
                return taken;
        }
    }

    public void Return(long amount) => Interlocked.Add(ref this.remaining, amount);
}

/// <summary>
/// The receiving half of one flow-controlled stream: frames are queued as they arrive and delivered by a pump of the
/// stream's own, which grants the sender more credit as the destination takes the bytes.
/// <para>
/// This is what keeps a slow destination from stopping the tunnel. Without it the frame loop delivered each frame
/// itself and waited for the destination - a public client that stopped reading, a handler that never read its upload
/// - and every other stream waited behind it. Here the loop only queues, and the sender's window bounds the queue.
/// </para>
/// <para>
/// The window starts at the size this end declared and grows, as HTTP/2 implementations do, while it is the
/// bottleneck: when half of it is delivered in less than a round trip, the destination is keeping up and the sender is
/// waiting on credit, so the window doubles - by granting the extra - up to a cap, and only while the tunnel's budget
/// allows. A stream whose destination stalls never grows, which is the one whose window would sit full in memory.
/// </para>
/// </summary>
sealed class StreamReceiver
{
    readonly int initialWindow;
    readonly int maxWindow;
    readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> deliver;
    readonly Func<int, ValueTask> grant;
    readonly TunnelRoundTrip? roundTrip;
    readonly StreamWindowBudget? budget;
    readonly Action? sampleRoundTrip;
    // Pooled: a busy tunnel queues a frame's worth for every frame, and each is handed back once delivered.
    readonly Channel<(byte[] Buffer, int Length)> queue = Channel.CreateUnbounded<(byte[] Buffer, int Length)>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );
    readonly CancellationTokenSource aborted = new();
    readonly Task pump;

    // The window the sender is held to: what this end declared, plus whatever it has grown by since.
    long window;

    // Bytes the sender has put on the stream that have not been granted back yet. The sender may never have more than
    // the window outstanding; more is a protocol violation.
    long outstanding;
    int stopped;

    /// <param name="window">The window this end declared, which the sender starts out held to.</param>
    /// <param name="deliver">
    /// Hands one chunk to the destination and completes once it has taken it. Returns false when the destination is
    /// gone for good; the rest is then discarded, and still granted, so the sender is never left waiting on it.
    /// </param>
    /// <param name="grant">Sends a WindowUpdate for that many bytes.</param>
    /// <param name="maxWindow">How far the window may grow; at or below <paramref name="window"/> it never grows.</param>
    /// <param name="roundTrip">The tunnel's round trip, which growth is judged against; null never grows.</param>
    /// <param name="budget">The tunnel's room for growth, shared across its streams; null is unlimited.</param>
    /// <param name="sampleRoundTrip">Asks for a round-trip sample when there is none yet.</param>
    public StreamReceiver(
        int window,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> deliver,
        Func<int, ValueTask> grant,
        int maxWindow = 0,
        TunnelRoundTrip? roundTrip = null,
        StreamWindowBudget? budget = null,
        Action? sampleRoundTrip = null
    )
    {
        this.window = window;
        this.initialWindow = window;
        this.maxWindow = Math.Max(window, maxWindow);
        this.deliver = deliver;
        this.grant = grant;
        this.roundTrip = roundTrip;
        this.budget = budget;
        this.sampleRoundTrip = sampleRoundTrip;
        this.pump = Task.Run(this.PumpAsync, CancellationToken.None);
    }

    /// <summary>Completes once everything queued has been delivered or discarded, after <see cref="Complete"/> or <see cref="Abort"/>.</summary>
    public Task Completion => this.pump;

    /// <summary>Bytes received and not yet granted back.</summary>
    public long Outstanding => Interlocked.Read(ref this.outstanding);

    /// <summary>The window the sender is held to now.</summary>
    public long Window => Interlocked.Read(ref this.window);

    /// <summary>
    /// Queues one frame's payload, copied - the frame's memory belongs to the reader. False when it takes the sender
    /// past its window: the peer is not honouring flow control, and the caller resets the stream.
    /// </summary>
    public bool TryEnqueue(ReadOnlySequence<byte> payload)
    {
        if (payload.IsEmpty)
            return true;

        var length = payload.Length;
        if (Interlocked.Add(ref this.outstanding, length) > Interlocked.Read(ref this.window))
            return false;

        // A stream already over takes no more, but the bytes still count as received so the arithmetic stays honest.
        if (Volatile.Read(ref this.stopped) != 0)
            return true;

        var buffer = ArrayPool<byte>.Shared.Rent((int)length);
        payload.CopyTo(buffer);

        if (!this.queue.Writer.TryWrite((buffer, (int)length)))
            ArrayPool<byte>.Shared.Return(buffer);

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
        var grown = 0L;
        var lastGrant = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            await foreach (var (buffer, length) in this.queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    // Delivered means copied on by the destination, so the buffer can go back as soon as it returns.
                    if (!discarding && Volatile.Read(ref this.stopped) == 0)
                        discarding = !await this.deliver(buffer.AsMemory(0, length), this.aborted.Token).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                pending += length;

                // In batches, as HTTP/2 does: a WindowUpdate per frame doubles the frame count for nothing. Half a
                // window keeps the sender from running dry while a batch is in flight.
                if (pending >= Interlocked.Read(ref this.window) / 2 && Volatile.Read(ref this.stopped) == 0)
                {
                    // The delivered bytes leave the count before the window moves. The frame loop checks arrivals
                    // against both at once, and a sender may still be spending credit it already holds: lowering the
                    // window first, with the count still including what was just delivered, made a legitimate frame
                    // look like an overrun and reset the stream. In this order every frame the sender can send is
                    // within the window at every instant - a shrink is never more than half of what was delivered.
                    Interlocked.Add(ref this.outstanding, -pending);

                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    var resize = this.Resize(now - lastGrant, pending);
                    grown += resize;
                    lastGrant = now;

                    await this.grant(pending + resize).ConfigureAwait(false);
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
            // Whatever an abort left queued goes back too, and so does the room the window grew into.
            while (this.queue.Reader.TryRead(out var left))
                ArrayPool<byte>.Shared.Return(left.Buffer);

            if (grown > 0)
                this.budget?.Return(grown);

            this.aborted.Dispose();
        }
    }

    /// <summary>
    /// Grows the window while it is the bottleneck, and shrinks a grown one once it is not. Returns the change, which is
    /// added to - or, shrinking, withheld from - the grant that is about to go out for <paramref name="delivered"/>.
    /// <para>
    /// Shrinking can only happen this way: credit already granted is the sender's to spend, so the window comes down
    /// by granting back less than was delivered. When half the window took two round trips or more to go through, the
    /// destination or the sender is the limit and the window is bigger than it needs to be; giving the room back to
    /// the tunnel's budget lets a transfer that is window-bound grow instead. A stream that falls idle keeps its window
    /// - the sender could still use all of it - until it closes, or is busy again and slower.
    /// </para>
    /// </summary>
    int Resize(long elapsed, int delivered)
    {
        var current = Interlocked.Read(ref this.window);
        if (current > this.initialWindow
            && this.roundTrip?.Estimate is { } estimate
            && elapsed >= 2 * estimate.TotalSeconds * System.Diagnostics.Stopwatch.Frequency)
        {
            // Lowered before the smaller grant goes out, so the sender can never be ahead of it. At most half of what
            // was delivered, so the stream always gets credit back.
            var shrink = (int)Math.Min(current - this.initialWindow, delivered / 2);
            Interlocked.Add(ref this.window, -shrink);
            this.budget?.Return(shrink);
            return -shrink;
        }

        return this.Grow(elapsed);
    }

    /// <summary>
    /// Grows the window when half of it was delivered in less than a round trip - the sender is waiting on credit, not
    /// the destination on data - within the cap and the tunnel's budget. Returns the bytes it grew by, which go out
    /// with the grant. The window is raised before the credit, so the sender can never be ahead of it.
    /// </summary>
    int Grow(long elapsed)
    {
        var current = Interlocked.Read(ref this.window);
        if (current >= this.maxWindow || this.roundTrip is not { } roundTrip)
            return 0;

        // Every time, not only before the first sample: the round trip changes - a phone moving between networks -
        // and the estimator itself decides how often a fresh sample is worth a Ping.
        this.sampleRoundTrip?.Invoke();

        if (roundTrip.Estimate is not { } estimate)
            return 0;

        var roundTripTicks = estimate.TotalSeconds * System.Diagnostics.Stopwatch.Frequency;
        if (elapsed >= roundTripTicks)
            return 0;

        // Half the window went in elapsed; a window that lasts a whole round trip is that many times bigger. Grown
        // toward it - at least doubled, at most eight times in one step, so a burst cannot overshoot far - rather than
        // only doubled, which on a fresh stream took a round trip per doubling to reach a fast link's speed.
        var factor = Math.Clamp(roundTripTicks / Math.Max(1, elapsed), 2, 8);
        var growth = (int)Math.Min(current * (factor - 1), this.maxWindow - current);

        // As much of it as the tunnel has room for: a budget nearly spent still lets a window grow a little.
        if (this.budget is not null)
            growth = (int)this.budget.TakeUpTo(growth);

        if (growth <= 0)
            return 0;

        Interlocked.Add(ref this.window, growth);
        return growth;
    }
}

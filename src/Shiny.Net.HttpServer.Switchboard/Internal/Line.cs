using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace Shiny.Net.HttpServer.Switchboard.Internal;

/// <summary>One queued event: its per-line sequence number, name and pre-rendered data.</summary>
readonly record struct Outbound(long Id, string Event, string Data);

/// <summary>What the connect handler drains while its stream is open.</summary>
sealed record Attachment(ChannelReader<Outbound> Reader, int Generation, bool Missed);

/// <summary>
/// One line: identity, groups, per-line state, the replay ring, and the queue of the stream that is
/// currently draining it — if one is.
/// <para>
/// A line outlives any one stream. The stream can drop and be replaced (a resume), and the line
/// carries on with the same id, groups and <c>Items</c>. Every send takes a sequence number and goes
/// into the replay ring whether or not a stream is attached, which is the whole of the resume story:
/// the client says which number it saw last, and the ring supplies the rest.
/// </para>
/// </summary>
sealed class Line
{
    readonly object sync = new();
    readonly HashSet<string> groups = new(StringComparer.Ordinal);
    readonly ReplayRing ring;
    readonly int bufferSize;
    readonly CancellationTokenSource closed = new();

    Channel<Outbound>? channel;
    long sequence;
    int generation;
    long detachedAt;
    string? closeData;
    int closedGeneration = -1;

    public Line(
        string id,
        ClaimsPrincipal principal,
        string? userId,
        int replayBufferSize,
        int bufferSize,
        int maxParallelInvocations,
        string protocol,
        IPAddress? remoteAddress
    )
    {
        this.Id = id;
        this.Token = NewToken();
        this.PrincipalName = principal.Identity?.Name;
        this.WasAuthenticated = principal.Identity?.IsAuthenticated == true;
        this.UserId = userId;
        this.ring = new ReplayRing(replayBufferSize);
        this.bufferSize = Math.Max(1, bufferSize);
        this.Gate = new SemaphoreSlim(Math.Max(1, maxParallelInvocations));
        this.MaxParallelInvocations = Math.Max(1, maxParallelInvocations);
        this.Protocol = protocol;
        this.RemoteAddress = remoteAddress;
        this.ConnectedAt = DateTimeOffset.UtcNow;

        // Counted from now rather than from zero: between Open and the first Attach the line has no stream,
        // and a sweeper that reads a zero here sees a line detached since boot and expires it.
        this.detachedAt = Environment.TickCount64;
    }

    public string Id { get; }

    /// <summary>
    /// The secret half of the line's identity. 128 random bits, because anyone holding it can speak
    /// as this line — it is a bearer token in everything but name. The id is the public half.
    /// </summary>
    public string Token { get; }

    /// <summary>Who connected. Every later request on the line must be the same principal.</summary>
    public string? PrincipalName { get; }

    public bool WasAuthenticated { get; }

    public string? UserId { get; }

    public DateTimeOffset ConnectedAt { get; }

    public string Protocol { get; private set; }

    public IPAddress? RemoteAddress { get; private set; }

    public int Resumes { get; private set; }

    public Dictionary<object, object?> Items { get; } = new();

    /// <summary>Bounds concurrent calls from this line.</summary>
    public SemaphoreSlim Gate { get; }

    // Only one taker of the whole gate at a time. Two of them each holding part of it — a disconnect and
    // a resume's OnReconnectedAsync, say — would each wait forever for the slots the other has.
    readonly SemaphoreSlim wholeGate = new(1, 1);

    /// <summary>
    /// Takes every slot of the invocation gate, so the lifecycle callback that follows runs with no call
    /// beside it. Returns how many slots it got — fewer than <see cref="MaxParallelInvocations"/> when
    /// <paramref name="cancellationToken"/> fired first — and the caller releases exactly that many.
    /// </summary>
    public async ValueTask<int> AcquireWholeGateAsync(CancellationToken cancellationToken)
    {
        var acquired = 0;

        try
        {
            await this.wholeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }

        try
        {
            for (; acquired < this.MaxParallelInvocations; acquired++)
                await this.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            this.wholeGate.Release();
        }

        return acquired;
    }

    /// <summary>
    /// Takes the whole gate of a line nobody else can see yet, so it is free by construction. Held
    /// through <c>OnConnectedAsync</c>: the client has the token from the <c>connected</c> event before the
    /// callback runs, and a call it makes straight away must wait for the callback rather than run beside it.
    /// </summary>
    public void HoldGateForConnect()
    {
        for (var i = 0; i < this.MaxParallelInvocations; i++)
            this.Gate.Wait(0);
    }

    // ---- calls in flight ----

    readonly object calls = new();
    int running;
    TaskCompletionSource? drained;

    /// <summary>
    /// Counts a call for as long as it runs — including a stream, which gives the gate back once its
    /// headers are out but keeps running its body after that.
    /// </summary>
    public void EnterCall()
    {
        lock (this.calls)
            this.running++;
    }

    public void ExitCall()
    {
        TaskCompletionSource? done = null;

        lock (this.calls)
        {
            this.running--;
            if (this.running == 0)
            {
                done = this.drained;
                this.drained = null;
            }
        }

        done?.TrySetResult();
    }

    /// <summary>Completes once no call is running on this line.</summary>
    public Task CallsDrained()
    {
        lock (this.calls)
        {
            if (this.running == 0)
                return Task.CompletedTask;

            this.drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return this.drained.Task;
        }
    }

    public int MaxParallelInvocations { get; }

    public CancellationToken Closed => this.closed.Token;

    public bool IsClosed { get; private set; }

    public LineStatus Status
    {
        get
        {
            lock (this.sync)
                return this.channel is null ? LineStatus.Detached : LineStatus.Attached;
        }
    }

    /// <summary><see cref="Environment.TickCount64"/> when the last stream ended, or null while attached.</summary>
    public long? DetachedAt
    {
        get
        {
            lock (this.sync)
                return this.channel is null && !this.IsClosed ? this.detachedAt : null;
        }
    }

    // ---- groups ----

    public bool JoinGroup(string group)
    {
        lock (this.groups)
            return this.groups.Add(group);
    }

    public bool LeaveGroup(string group)
    {
        lock (this.groups)
            return this.groups.Remove(group);
    }

    public bool InGroup(string group)
    {
        lock (this.groups)
            return this.groups.Contains(group);
    }

    public IReadOnlyCollection<string> Groups
    {
        get
        {
            lock (this.groups)
                return [.. this.groups];
        }
    }

    // ---- sending ----

    /// <summary>
    /// Queues an event. Returns false when the line is closed. When the attached stream's queue is
    /// full the stream is cut — <paramref name="overflowed"/> tells the caller — and the event stays
    /// in the ring for the resume that follows.
    /// </summary>
    public bool Send(string eventName, string data, out bool overflowed)
    {
        overflowed = false;

        lock (this.sync)
        {
            if (this.IsClosed)
                return false;

            var item = new Outbound(++this.sequence, eventName, data);
            this.ring.Add(item);

            if (this.channel is { } current && !current.Writer.TryWrite(item))
            {
                overflowed = true;
                this.DetachCore();
            }

            return true;
        }
    }

    public bool Send(string eventName, string data) => this.Send(eventName, data, out _);

    // ---- streams ----

    /// <summary>
    /// Attaches a new stream, replacing any stream still attached. With <paramref name="lastEventId"/>
    /// this is a resume: everything after it that the ring still holds is queued first, and
    /// <see cref="Attachment.Missed"/> says whether the ring could cover the whole gap.
    /// </summary>
    public Attachment? Attach(long? lastEventId, string protocol, IPAddress? remoteAddress)
    {
        lock (this.sync)
        {
            if (this.IsClosed)
                return null;

            // A client that noticed the drop before the server did comes back while the old stream is
            // still "attached". The new one wins; completing the old queue ends its writer loop.
            this.channel?.Writer.TryComplete();

            var capacity = this.bufferSize + this.ring.Count;
            var next = Channel.CreateBounded<Outbound>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            var missed = false;
            if (lastEventId is { } last)
            {
                missed = this.ring.Replay(last, this.sequence, next.Writer);
                this.Resumes++;
            }

            this.channel = next;
            this.generation++;
            this.Protocol = protocol;
            this.RemoteAddress = remoteAddress;

            return new Attachment(next.Reader, this.generation, missed);
        }
    }

    /// <summary>
    /// Called when a stream ends. Only detaches if that stream is still the current one — a resume
    /// may already have replaced it.
    /// </summary>
    public void Detach(int generation)
    {
        lock (this.sync)
        {
            if (generation == this.generation && !this.IsClosed)
                this.DetachCore();
        }
    }

    /// <summary>Ends the current stream without closing the line — what a network drop looks like. For tests.</summary>
    internal void DropStream()
    {
        lock (this.sync)
        {
            if (!this.IsClosed && this.channel is not null)
                this.DetachCore();
        }
    }

    void DetachCore()
    {
        this.channel?.Writer.TryComplete();
        this.channel = null;
        this.detachedAt = Environment.TickCount64;
    }

    // ---- ordering ----

    readonly object turns = new();
    readonly SortedDictionary<long, TaskCompletionSource> waiting = [];
    readonly SortedSet<long> forfeited = [];
    long nextTurn = 1;

    /// <summary>
    /// Waits until every lower-numbered call from this line has run, or until
    /// <paramref name="gap"/> passes with a lower number still missing — a request that never
    /// arrives must not hold the line forever.
    /// </summary>
    public async ValueTask WaitTurnAsync(long sequence, TimeSpan gap, CancellationToken cancellationToken)
    {
        TaskCompletionSource turn;

        lock (this.turns)
        {
            if (sequence <= this.nextTurn)
                return;

            turn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            this.waiting[sequence] = turn;
        }

        try
        {
            await turn.Task.WaitAsync(gap, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (this.turns)
            {
                this.waiting.Remove(sequence);
                if (this.nextTurn < sequence)
                    this.nextTurn = sequence;
            }
        }
        catch
        {
            lock (this.turns)
                this.waiting.Remove(sequence);

            throw;
        }
    }

    /// <summary>Marks a call as done with its turn, releasing the next one if it is waiting.</summary>
    public void EndTurn(long sequence)
    {
        lock (this.turns)
        {
            if (sequence >= this.nextTurn)
                this.nextTurn = sequence + 1;

            this.AdvanceTurns();
        }
    }

    /// <summary>
    /// Gives up the turn of a call that will never run — refused before it started, or cancelled while it
    /// waited. Its number still counts as done, so the call after it does not wait out the ordering timeout
    /// for it; but it does not hurry anything along either, because the calls numbered before it may not
    /// have run yet.
    /// </summary>
    public void ForfeitTurn(long sequence)
    {
        lock (this.turns)
        {
            if (sequence < this.nextTurn)
                return;

            if (sequence == this.nextTurn)
                this.nextTurn = sequence + 1;
            else
                this.forfeited.Add(sequence);

            this.AdvanceTurns();
        }
    }

    // Called under the turns lock.
    void AdvanceTurns()
    {
        // A number given up early is done as soon as everything before it is.
        while (this.forfeited.Remove(this.nextTurn))
            this.nextTurn++;

        this.forfeited.RemoveWhere(s => s < this.nextTurn);

        // Anything at or below the new turn may go — including numbers a timeout skipped past.
        while (this.waiting.Count > 0)
        {
            var first = this.waiting.First();
            if (first.Key > this.nextTurn)
                break;

            this.waiting.Remove(first.Key);
            first.Value.TrySetResult();
        }
    }

    // ---- client results ----

    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> results = new(StringComparer.Ordinal);

    /// <summary>Registers a question for the client. Null when too many are already waiting.</summary>
    public (string Id, Task<JsonElement> Answer)? RegisterResult(int limit)
    {
        if (this.results.Count >= limit)
            return null;

        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.results[id] = answer;

        // A close that raced the registration has already failed everything it could see.
        if (this.IsClosed)
            answer.TrySetException(new LineClosedException("The line closed before the client answered."));

        return (id, answer.Task);
    }

    public void RemoveResult(string id) => this.results.TryRemove(id, out _);

    public bool CompleteResult(string id, JsonElement result)
        => this.results.TryRemove(id, out var answer) && answer.TrySetResult(result);

    public bool FailResult(string id, string error)
        => this.results.TryRemove(id, out var answer) && answer.TrySetException(new SwitchboardException(error));

    void FailAllResults()
    {
        foreach (var entry in this.results)
        {
            if (this.results.TryRemove(entry.Key, out var answer))
                answer.TrySetException(new LineClosedException("The line closed before the client answered."));
        }
    }

    // ---- closing ----

    /// <summary>
    /// Marks the line closed and ends its stream. The <c>close</c> event is not queued (the queue may
    /// be full) but kept aside for the writer loop of the current stream to send last. Returns false
    /// when the line was already closed.
    /// </summary>
    public bool TryClose(string closeData)
    {
        lock (this.sync)
        {
            if (this.IsClosed)
                return false;

            this.IsClosed = true;

            if (this.channel is { } current)
            {
                this.closeData = closeData;
                this.closedGeneration = this.generation;
                current.Writer.TryComplete();
                this.channel = null;
            }
        }

        this.FailAllResults();
        return true;
    }

    /// <summary>
    /// Closes the line if it is still detached and has been for at least <paramref name="windowMs"/>.
    /// The check and the close are one step under the line's lock, so a resume that attaches after the
    /// sweeper looked cannot be closed out from under its client.
    /// </summary>
    public bool TryCloseDetached(long now, long windowMs)
    {
        lock (this.sync)
        {
            if (this.IsClosed || this.channel is not null || now - this.detachedAt < windowMs)
                return false;

            // No stream is attached, so there is nobody to send a close event to.
            this.IsClosed = true;
        }

        this.FailAllResults();
        return true;
    }

    /// <summary>The close event for a stream that just ended, if the line was closed while it was the current one.</summary>
    public string? CloseDataFor(int generation)
    {
        lock (this.sync)
            return this.closedGeneration == generation ? this.closeData : null;
    }

    public void CancelClosed()
    {
        try
        {
            this.closed.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public LineInfo ToInfo() => new(
        this.Id,
        this.UserId,
        this.Groups,
        this.ConnectedAt,
        this.Status,
        this.Resumes,
        this.Protocol,
        this.RemoteAddress
    );

    static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
}

/// <summary>The last N events sent to a line, oldest first.</summary>
sealed class ReplayRing(int capacity)
{
    readonly Outbound[] items = new Outbound[Math.Max(0, capacity)];
    int start;

    public int Count { get; private set; }

    public void Add(Outbound item)
    {
        if (this.items.Length == 0)
            return;

        if (this.Count < this.items.Length)
        {
            this.items[(this.start + this.Count) % this.items.Length] = item;
            this.Count++;
            return;
        }

        this.items[this.start] = item;
        this.start = (this.start + 1) % this.items.Length;
    }

    /// <summary>
    /// Queues every event after <paramref name="lastEventId"/>. Returns true when some of them had
    /// already fallen out of the ring — the client has a gap it cannot fill and should know it.
    /// </summary>
    public bool Replay(long lastEventId, long currentSequence, ChannelWriter<Outbound> writer)
    {
        if (lastEventId >= currentSequence)
            return false;

        var oldest = this.Count == 0 ? currentSequence + 1 : this.items[this.start].Id;
        var missed = lastEventId + 1 < oldest;

        for (var i = 0; i < this.Count; i++)
        {
            var item = this.items[(this.start + i) % this.items.Length];
            if (item.Id > lastEventId)
                writer.TryWrite(item);
        }

        return missed;
    }
}

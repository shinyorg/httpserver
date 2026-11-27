using System.Buffers;
using System.Buffers.Binary;
using Shiny.Net.HttpServer.Tunneling;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>The flow-control pieces on their own: the Settings encoding, the send window and the per-stream receiver.</summary>
public class TunnelFlowControlUnitTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static ReadOnlySequence<byte> Bytes(params byte[] bytes) => new(bytes);

    // ---- Settings ----

    [Fact]
    public void Settings_round_trip()
    {
        var settings = new TunnelSettings(1, 512 * 1024, 128);

        var decoded = TunnelSettings.Decode(new ReadOnlySequence<byte>(settings.Encode()));

        Assert.Equal(settings, decoded);
    }

    [Fact]
    public void Settings_entries_a_reader_does_not_know_are_skipped()
    {
        // A later version adds entry 99; this reader must still get what it understands.
        var known = new TunnelSettings(1, 256 * 1024, 4).Encode();
        var extra = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(extra, 99);
        BinaryPrimitives.WriteUInt32BigEndian(extra.AsSpan(2), 12345);

        var decoded = TunnelSettings.Decode(new ReadOnlySequence<byte>([.. extra, .. known]));

        Assert.Equal(new TunnelSettings(1, 256 * 1024, 4), decoded);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    public void Settings_that_are_not_whole_entries_are_refused(int length)
        => Assert.Throws<TunnelProtocolException>(() => TunnelSettings.Decode(new ReadOnlySequence<byte>(new byte[length])));

    [Fact]
    public void Settings_missing_an_entry_are_refused()
    {
        var partial = new TunnelSettings(1, 256 * 1024, 4).Encode()[..12];
        Assert.Throws<TunnelProtocolException>(() => TunnelSettings.Decode(new ReadOnlySequence<byte>(partial)));
    }

    [Theory]
    [InlineData(0, 256 * 1024, 4)]
    [InlineData(1, 1024, 4)]
    [InlineData(1, (1 << 30) + 1, 4)]
    [InlineData(1, 256 * 1024, 0)]
    public void Settings_out_of_range_are_refused(int version, int window, int streams)
    {
        var encoded = new TunnelSettings(version, window, streams).Encode();
        Assert.Throws<TunnelProtocolException>(() => TunnelSettings.Decode(new ReadOnlySequence<byte>(encoded)));
    }

    [Fact]
    public void Window_updates_round_trip_and_reject_zero_and_malformed()
    {
        Assert.Equal(4096, TunnelSettings.DecodeWindowUpdate(new ReadOnlySequence<byte>(TunnelSettings.EncodeWindowUpdate(4096))));
        Assert.Null(TunnelSettings.DecodeWindowUpdate(Bytes(0, 0, 0, 0)));
        Assert.Null(TunnelSettings.DecodeWindowUpdate(Bytes(0, 0, 1)));
        Assert.Null(TunnelSettings.DecodeWindowUpdate(Bytes(0x80, 0, 0, 0)));
    }

    // ---- Send window ----

    [Fact]
    public async Task A_send_window_grants_up_to_what_is_available()
    {
        var window = new StreamSendWindow(100);

        Assert.Equal(60, await window.ReserveAsync(60, Token));
        Assert.Equal(40, await window.ReserveAsync(60, Token));
        Assert.Equal(0, window.Available);
    }

    [Fact]
    public async Task A_send_window_waits_for_credit_and_wakes_when_it_comes()
    {
        var window = new StreamSendWindow(10);
        Assert.Equal(10, await window.ReserveAsync(10, Token));

        var waiting = window.ReserveAsync(10, Token).AsTask();
        await Task.Delay(50, Token);
        Assert.False(waiting.IsCompleted);

        Assert.True(window.Add(7));
        Assert.Equal(7, await waiting.WaitAsync(TimeSpan.FromSeconds(5), Token));
    }

    [Fact]
    public async Task Closing_a_send_window_wakes_a_waiting_sender_with_nothing()
    {
        var window = new StreamSendWindow(1);
        Assert.Equal(1, await window.ReserveAsync(1, Token));

        var waiting = window.ReserveAsync(10, Token).AsTask();
        window.Close();

        Assert.Equal(0, await waiting.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Equal(0, await window.ReserveAsync(10, Token));
    }

    [Fact]
    public void A_send_window_refuses_credit_that_would_overflow_it()
    {
        var window = new StreamSendWindow(int.MaxValue - 10);

        Assert.False(window.Add(11));
        Assert.True(window.Add(10));
    }

    [Fact]
    public async Task A_send_window_waiter_can_be_cancelled()
    {
        var window = new StreamSendWindow(1);
        Assert.Equal(1, await window.ReserveAsync(1, Token));

        using var cancel = new CancellationTokenSource();
        var waiting = window.ReserveAsync(10, cancel.Token).AsTask();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    // ---- Receiver ----

    sealed class Recorder
    {
        public readonly List<byte> Delivered = [];
        public readonly List<int> Grants = [];
        public TaskCompletionSource Gate { get; set; } = CompletedGate();
        public bool Accepting { get; set; } = true;

        static TaskCompletionSource CompletedGate()
        {
            var gate = new TaskCompletionSource();
            gate.SetResult();
            return gate;
        }

        public async ValueTask<bool> DeliverAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            await this.Gate.Task.WaitAsync(cancellationToken);
            lock (this.Delivered)
                this.Delivered.AddRange(chunk.ToArray());
            return this.Accepting;
        }

        public ValueTask GrantAsync(int increment)
        {
            lock (this.Grants)
                this.Grants.Add(increment);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_receiver_delivers_in_order_and_grants_in_batches()
    {
        var recorder = new Recorder();
        var receiver = new StreamReceiver(100, recorder.DeliverAsync, recorder.GrantAsync);

        for (byte i = 0; i < 10; i++)
            Assert.True(receiver.TryEnqueue(new ReadOnlySequence<byte>(Enumerable.Repeat(i, 10).ToArray())));

        receiver.Complete();
        await receiver.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.Equal(100, recorder.Delivered.Count);
        Assert.Equal(Enumerable.Range(0, 10).SelectMany(i => Enumerable.Repeat((byte)i, 10)), recorder.Delivered);

        // Batches of half a window: 50, then 50.
        Assert.Equal([50, 50], recorder.Grants);
    }

    [Fact]
    public async Task A_receiver_refuses_a_sender_that_goes_past_its_window()
    {
        var recorder = new Recorder { Gate = new TaskCompletionSource() };
        var receiver = new StreamReceiver(100, recorder.DeliverAsync, recorder.GrantAsync);

        Assert.True(receiver.TryEnqueue(new ReadOnlySequence<byte>(new byte[100])));
        Assert.False(receiver.TryEnqueue(new ReadOnlySequence<byte>(new byte[1])));

        receiver.Abort();
        await receiver.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task A_receiver_takes_more_once_it_has_granted_credit_back()
    {
        var recorder = new Recorder();
        var receiver = new StreamReceiver(100, recorder.DeliverAsync, recorder.GrantAsync);

        Assert.True(receiver.TryEnqueue(new ReadOnlySequence<byte>(new byte[100])));
        await Wait.UntilAsync(() => receiver.Outstanding == 0, Token);

        // The window is free again, all of it.
        Assert.True(receiver.TryEnqueue(new ReadOnlySequence<byte>(new byte[100])));

        receiver.Complete();
        await receiver.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(200, recorder.Delivered.Count);
    }

    [Fact]
    public async Task A_receiver_whose_destination_is_gone_still_grants_what_it_discards()
    {
        var recorder = new Recorder { Accepting = false };
        var receiver = new StreamReceiver(100, recorder.DeliverAsync, recorder.GrantAsync);

        for (var i = 0; i < 4; i++)
            Assert.True(receiver.TryEnqueue(new ReadOnlySequence<byte>(new byte[25])));

        receiver.Complete();
        await receiver.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token);

        // Only the first chunk reached the destination; every byte was granted back, so the sender is never stuck.
        Assert.Equal(25, recorder.Delivered.Count);
        Assert.Equal(100, recorder.Grants.Sum());
    }

    [Fact]
    public async Task Aborting_a_receiver_drops_what_is_queued_and_cancels_a_delivery_in_progress()
    {
        var recorder = new Recorder { Gate = new TaskCompletionSource() };
        var receiver = new StreamReceiver(100, recorder.DeliverAsync, recorder.GrantAsync);

        Assert.True(receiver.TryEnqueue(new ReadOnlySequence<byte>(new byte[10])));
        Assert.True(receiver.TryEnqueue(new ReadOnlySequence<byte>(new byte[10])));

        receiver.Abort();
        await receiver.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.Empty(recorder.Delivered);
        Assert.Empty(recorder.Grants);
    }

    // ---- Window growth ----

    static TunnelRoundTrip RoundTripOf(TimeSpan duration)
    {
        var roundTrip = new TunnelRoundTrip();
        Assert.True(roundTrip.TryBeginSample());
        Thread.Sleep(duration);
        roundTrip.OnPong();
        return roundTrip;
    }

    /// <summary>Feeds a receiver as a sender would: never more than the credit it has been granted.</summary>
    static async Task FeedAsync(StreamReceiver receiver, StreamSendWindow sender, long total)
    {
        var sent = 0L;
        while (sent < total)
        {
            var take = await sender.ReserveAsync((int)Math.Min(TunnelProtocol.MaxPayloadLength, total - sent), Token);
            Assert.True(receiver.TryEnqueue(new ReadOnlySequence<byte>(new byte[take])));
            sent += take;
        }
    }

    [Fact]
    public async Task A_window_whose_destination_keeps_up_grows_while_it_is_the_bottleneck()
    {
        const int Initial = 64 * 1024;
        var roundTrip = RoundTripOf(TimeSpan.FromMilliseconds(200));
        var sender = new StreamSendWindow(Initial);
        var budget = new StreamWindowBudget(64L * 1024 * 1024);

        // A destination that takes everything at once, granting back to the sender as a real peer would.
        StreamReceiver receiver = null!;
        receiver = new StreamReceiver(
            Initial,
            (_, _) => ValueTask.FromResult(true),
            increment =>
            {
                sender.Add(increment);
                return ValueTask.CompletedTask;
            },
            maxWindow: 4 * 1024 * 1024,
            roundTrip,
            budget
        );

        await FeedAsync(receiver, sender, 8L * 1024 * 1024);
        receiver.Complete();
        await receiver.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.True(receiver.Window > Initial, $"The window stayed at {receiver.Window}.");
        Assert.True(receiver.Window <= 4 * 1024 * 1024);

        // The room it grew into is given back once the stream ends.
        Assert.Equal(64L * 1024 * 1024, budget.Remaining);
    }

    [Fact]
    public async Task A_window_whose_destination_is_slow_never_grows()
    {
        const int Initial = 64 * 1024;
        var roundTrip = RoundTripOf(TimeSpan.FromMilliseconds(5));
        var sender = new StreamSendWindow(Initial);

        // Each chunk takes far longer than a round trip to be taken: the destination, not the window, is the limit.
        var receiver = new StreamReceiver(
            Initial,
            async (_, ct) =>
            {
                await Task.Delay(50, ct);
                return true;
            },
            increment =>
            {
                sender.Add(increment);
                return ValueTask.CompletedTask;
            },
            maxWindow: 4 * 1024 * 1024,
            roundTrip,
            new StreamWindowBudget(64L * 1024 * 1024)
        );

        await FeedAsync(receiver, sender, 512 * 1024);
        receiver.Complete();
        await receiver.Completion.WaitAsync(TimeSpan.FromSeconds(20), Token);

        Assert.Equal(Initial, receiver.Window);
    }

    [Fact]
    public async Task A_window_does_not_grow_past_the_tunnels_budget()
    {
        const int Initial = 64 * 1024;
        var roundTrip = RoundTripOf(TimeSpan.FromMilliseconds(200));
        var sender = new StreamSendWindow(Initial);
        var budget = new StreamWindowBudget(Initial);

        var receiver = new StreamReceiver(
            Initial,
            (_, _) => ValueTask.FromResult(true),
            increment =>
            {
                sender.Add(increment);
                return ValueTask.CompletedTask;
            },
            maxWindow: 4 * 1024 * 1024,
            roundTrip,
            budget
        );

        await FeedAsync(receiver, sender, 4L * 1024 * 1024);

        // Room for one window's worth of growth: it doubled, once, and took all of it.
        Assert.True(receiver.Window <= 2 * Initial, $"The window grew to {receiver.Window}.");

        receiver.Complete();
        await receiver.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(Initial, budget.Remaining);
    }

    [Fact]
    public async Task A_window_with_no_round_trip_estimate_asks_for_one_and_does_not_grow_yet()
    {
        const int Initial = 64 * 1024;
        var asked = 0;
        var sender = new StreamSendWindow(Initial);

        var receiver = new StreamReceiver(
            Initial,
            (_, _) => ValueTask.FromResult(true),
            increment =>
            {
                sender.Add(increment);
                return ValueTask.CompletedTask;
            },
            maxWindow: 4 * 1024 * 1024,
            new TunnelRoundTrip(),
            new StreamWindowBudget(64L * 1024 * 1024),
            () => Interlocked.Increment(ref asked)
        );

        await FeedAsync(receiver, sender, 1024 * 1024);
        receiver.Complete();
        await receiver.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Equal(Initial, receiver.Window);
        Assert.True(asked > 0);
    }

    [Fact]
    public void A_round_trip_is_sampled_once_at_a_time_and_smoothed()
    {
        var roundTrip = new TunnelRoundTrip();
        Assert.Null(roundTrip.Estimate);

        Assert.True(roundTrip.TryBeginSample());
        Assert.False(roundTrip.TryBeginSample());

        Thread.Sleep(20);
        roundTrip.OnPong();
        var first = roundTrip.Estimate;
        Assert.NotNull(first);
        Assert.True(first.Value >= TimeSpan.FromMilliseconds(15));

        // Fresh: no new sample for a while. A Pong with no Ping out is ignored.
        Assert.False(roundTrip.TryBeginSample());
        roundTrip.OnPong();
        Assert.Equal(first, roundTrip.Estimate);
    }
}

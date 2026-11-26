namespace Shiny.Net.HttpServer.Http1;

/// <summary>
/// Puts a deadline on one pending pipe read or flush at a time.
/// <para>
/// Expiry cancels through the pipe's own <c>CancelPendingRead</c> / <c>CancelPendingFlush</c>, so the
/// hot path arms and disarms one timer per connection instead of allocating a linked
/// <see cref="CancellationTokenSource"/> for every read and every write.
/// </para>
/// <para>
/// Expiry is final. Once it has fired the connection is not to be trusted with another exchange —
/// the cancel may even have landed on the next operation rather than the one it was meant for — so
/// <see cref="Expired"/> stays set and the connection closes.
/// </para>
/// </summary>
sealed class PipeOperationTimer : IDisposable
{
    readonly Action cancel;
    readonly Timer timer;
    volatile bool expired;
    bool armed;

    public PipeOperationTimer(Action cancel)
    {
        this.cancel = cancel;
        this.timer = new Timer(static state => ((PipeOperationTimer)state!).Fire(), this, Timeout.Infinite, Timeout.Infinite);
    }

    public bool Expired => this.expired;

    /// <summary>Arms the deadline. A zero or infinite timeout leaves the operation unbounded.</summary>
    public void Start(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            return;

        this.armed = true;
        this.timer.Change(timeout, Timeout.InfiniteTimeSpan);
    }

    public void Stop()
    {
        if (!this.armed)
            return;

        this.armed = false;
        this.timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    public void Dispose() => this.timer.Dispose();

    void Fire()
    {
        this.expired = true;
        this.cancel();
    }
}

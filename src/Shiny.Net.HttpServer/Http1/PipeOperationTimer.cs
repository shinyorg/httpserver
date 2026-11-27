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
    readonly object gate = new();
    volatile bool expired;
    bool armed;
    bool disposed;
    long deadline;

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

        lock (this.gate)
        {
            if (this.disposed)
                return;

            this.armed = true;
            this.deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            this.timer.Change(timeout, Timeout.InfiniteTimeSpan);
        }
    }

    public void Stop()
    {
        lock (this.gate)
        {
            if (!this.armed)
                return;

            this.armed = false;
            this.timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        lock (this.gate)
        {
            this.disposed = true;
            this.armed = false;
        }

        this.timer.Dispose();
    }

    /// <summary>
    /// A timer callback can already be queued when <see cref="Stop"/> runs - on a busy thread pool, well after the
    /// operation it was meant for has finished. Acting on it then would mark a healthy connection expired and cancel
    /// whatever read or flush happened to come next, or, once the pipe is completed, throw on a timer thread and take
    /// the process down. So it only counts while still armed and past the deadline of the current arming.
    /// </summary>
    internal void Fire()
    {
        lock (this.gate)
        {
            if (!this.armed || this.disposed || Environment.TickCount64 < this.deadline)
                return;

            this.armed = false;
            this.expired = true;
        }

        try
        {
            this.cancel();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // The pipe finished underneath the timer; there is nothing left to cancel.
        }
    }
}

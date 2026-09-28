using System.Diagnostics;

namespace Shiny.Net.HttpServer.Tus.Internal;

/// <summary>
/// One request per upload at a time, with a way for a newcomer to ask the current holder to leave.
/// <para>
/// Mutual exclusion alone would be correct and useless on a phone: the request holding the upload
/// is usually the one whose connection just died, and it will hold on until a read timeout notices
/// - minutes, while the client retries into a wall of 423s. So the holder's lease carries a token
/// its body read is bound to, and a newcomer cancels it. The holder keeps what it received, lets
/// go, and the newcomer - normally the same client resuming - takes over from the byte it stopped at.
/// </para>
/// </summary>
sealed class TusLockManager
{
    readonly object gate = new();
    readonly Dictionary<string, Lease> held = new(StringComparer.Ordinal);

    /// <summary>
    /// Takes the upload, asking the holder to let go and waiting up to <paramref name="wait"/> for it
    /// to. Null when it did not.
    /// </summary>
    public async ValueTask<Lease?> AcquireAsync(string id, TimeSpan wait, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        while (true)
        {
            Lease? current;

            lock (this.gate)
            {
                if (!this.held.TryGetValue(id, out current))
                {
                    var lease = new Lease(this, id);
                    this.held[id] = lease;
                    return lease;
                }
            }

            var remaining = wait - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
                return null;

            current.RequestRelease();

            try
            {
                await current.Released.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }
    }

    void Release(Lease lease)
    {
        lock (this.gate)
        {
            if (this.held.TryGetValue(lease.Id, out var current) && ReferenceEquals(current, lease))
                this.held.Remove(lease.Id);
        }
    }

    internal sealed class Lease : IDisposable
    {
        readonly TusLockManager owner;
        readonly CancellationTokenSource release = new();
        readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int disposed;

        public Lease(TusLockManager owner, string id)
        {
            this.owner = owner;
            this.Id = id;
            this.ReleaseRequested = this.release.Token;
        }

        public string Id { get; }

        /// <summary>Cancelled when another request wants this upload.</summary>
        public CancellationToken ReleaseRequested { get; }

        public Task Released => this.released.Task;

        public void RequestRelease()
        {
            if (Volatile.Read(ref this.disposed) != 0)
                return;

            try
            {
                this.release.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Released between the check and the cancel - which is what was being asked for.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0)
                return;

            this.owner.Release(this);
            this.released.TrySetResult();
            this.release.Dispose();
        }
    }
}

using Microsoft.Extensions.Logging;
using Sample.Maui.ViewModels;

namespace Sample.Maui.Server;

/// <summary>
/// Puts a count on the Traffic tab while somebody is looking at a different one.
/// <para>
/// It is a service rather than part of the Traffic view model on purpose: the whole point of the
/// badge is to say something while that tab — and therefore its view model — is not the one on
/// screen. <see cref="IMauiInitializeService"/> is the hook that gets it running at startup.
/// </para>
/// </summary>
public sealed class TrafficBadge(
    RequestLog log,
    INavigator navigator,
    IMainThread mainThread,
    ILogger<TrafficBadge> logger
) : IMauiInitializeService
{
    int unseen;

    public void Initialize(IServiceProvider services) => log.Added += (_, _) =>
    {
        Interlocked.Increment(ref this.unseen);
        this.Apply();
    };

    /// <summary>Called by the Traffic tab: what is on screen has been seen.</summary>
    public void Clear()
    {
        if (Interlocked.Exchange(ref this.unseen, 0) > 0)
            this.Apply();
    }

    void Apply()
    {
        var count = Volatile.Read(ref this.unseen);

        mainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                // Badges are set against a tab that exists in the running Shell, so the first
                // requests of a cold start can land before there is anything to badge.
                if (count == 0)
                    await navigator.ClearTabBadge<TrafficViewModel>();
                else
                    await navigator.SetTabBadge<TrafficViewModel>(count);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Tab badge not available");
            }
        });
    }
}

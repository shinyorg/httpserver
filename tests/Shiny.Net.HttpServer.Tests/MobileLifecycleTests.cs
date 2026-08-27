using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer.Mobile;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The retry that every start and restart the mobile lifecycle drives now goes through.
/// <para>
/// This is as far into the mobile package as a test off a device can reach, and it is said plainly
/// rather than worked around: <c>HttpServerLifecycleTask</c> lives inside <c>#if PLATFORM</c>, needs
/// Shiny's host to deliver the foreground and background callbacks, and its two most interesting
/// branches — the Android foreground service and the Apple resume — are platform partials that do
/// not compile on this target framework at all. Faking a host to get at them would test the fake.
/// </para>
/// <para>
/// What is testable is the piece those branches all hand their work to, and it happens to be the
/// piece that decides whether a bind refused by a settling network is a log line or a bug report
/// that says the server shut down for no reason.
/// </para>
/// </summary>
public class ServerTransitionRunnerTests
{
    /// <summary>The shipped policy with the waiting taken out of it — five attempts, milliseconds apart.</summary>
    static HttpServerLifecycleOptions Fast(int attempts = 5) => new()
    {
        RestartAttempts = attempts,
        RestartRetryDelay = TimeSpan.FromMilliseconds(5),
        MaxRestartRetryDelay = TimeSpan.FromMilliseconds(20)
    };

    /// <summary>Never gets past the first attempt on its own, so a test can decide when it does.</summary>
    static HttpServerLifecycleOptions Patient() => new()
    {
        RestartAttempts = 5,
        RestartRetryDelay = TimeSpan.FromSeconds(30),
        MaxRestartRetryDelay = TimeSpan.FromSeconds(30)
    };

    static Task Refused() => Task.FromException(new SocketException((int)SocketError.AddressNotAvailable));

    [Fact]
    public async Task Retries_a_bind_the_settling_network_refused()
    {
        var logger = new RecordingLogger<ServerTransitionRunnerTests>();
        using var runner = new ServerTransitionRunner(Fast(), logger);

        var attempts = 0;
        runner.Run("Rebind after a connectivity change", _ =>
        {
            attempts++;
            return attempts < 3 ? Refused() : Task.CompletedTask;
        });

        await runner.Completion;

        Assert.Equal(3, attempts);
        Assert.Empty(logger.At(LogLevel.Error));
    }

    /// <summary>The whole point: a listener that will not come back is an error someone sees, not silence.</summary>
    [Fact]
    public async Task Gives_up_at_error_with_the_reason_attached()
    {
        var logger = new RecordingLogger<ServerTransitionRunnerTests>();
        using var runner = new ServerTransitionRunner(Fast(), logger);

        var attempts = 0;
        runner.Run("Rebind after a connectivity change", _ =>
        {
            attempts++;
            return Refused();
        });

        await runner.Completion;

        Assert.Equal(5, attempts);

        var error = Assert.Single(logger.At(LogLevel.Error));

        Assert.IsType<SocketException>(error.Exception);
        Assert.Contains("Rebind after a connectivity change", error.Message, StringComparison.Ordinal);
        Assert.Contains("not listening", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Waits_longer_between_each_attempt()
    {
        var logger = new RecordingLogger<ServerTransitionRunnerTests>();
        using var runner = new ServerTransitionRunner(
            new HttpServerLifecycleOptions
            {
                RestartAttempts = 4,
                RestartRetryDelay = TimeSpan.FromMilliseconds(30),
                MaxRestartRetryDelay = TimeSpan.FromSeconds(1)
            },
            logger
        );

        var started = Stopwatch.StartNew();
        runner.Run("Rebind", _ => Refused());
        await runner.Completion;

        // 30 + 60 + 120 between four attempts. Asserted as a floor only: the machine can always be
        // slower, and a ceiling here would be a test that fails on a loaded CI box for no reason.
        Assert.True(started.ElapsedMilliseconds >= 200, $"backed off for only {started.ElapsedMilliseconds}ms");
    }

    /// <summary>Two connectivity changes a second apart are one event as far as the listener is concerned.</summary>
    [Fact]
    public async Task A_newer_change_supersedes_the_one_still_retrying()
    {
        var logger = new RecordingLogger<ServerTransitionRunnerTests>();
        using var runner = new ServerTransitionRunner(Patient(), logger);

        var stale = 0;
        var fresh = 0;
        var reached = new TaskCompletionSource();

        runner.Run("Rebind onto the old network", _ =>
        {
            stale++;
            reached.TrySetResult();
            return Refused();
        });

        await reached.Task;
        var abandoned = runner.Completion;

        runner.Run("Rebind onto the new network", _ =>
        {
            fresh++;
            return Task.CompletedTask;
        });

        await abandoned;
        await runner.Completion;

        Assert.Equal(1, stale);
        Assert.Equal(1, fresh);
        Assert.Empty(logger.At(LogLevel.Error));
    }

    /// <summary>A stop the app asked for must not be undone by a retry that was already in flight.</summary>
    [Fact]
    public async Task A_cancelled_run_stops_trying_and_is_not_reported_as_a_failure()
    {
        var logger = new RecordingLogger<ServerTransitionRunnerTests>();
        using var runner = new ServerTransitionRunner(Patient(), logger);

        var attempts = 0;
        var reached = new TaskCompletionSource();

        runner.Run("Rebind after a connectivity change", _ =>
        {
            attempts++;
            reached.TrySetResult();
            return Refused();
        });

        await reached.Task;
        runner.Cancel();
        await runner.Completion;

        // The one attempt it got to, and no error: an abandoned retry is not a failure, because
        // whatever cancelled it wanted the server down.
        Assert.Equal(1, attempts);
        Assert.Empty(logger.At(LogLevel.Error));
    }

    /// <summary>Setting the attempts to one is how an app asks for the old single-shot behaviour back.</summary>
    [Fact]
    public async Task One_attempt_is_still_an_error_rather_than_a_warning()
    {
        var logger = new RecordingLogger<ServerTransitionRunnerTests>();
        using var runner = new ServerTransitionRunner(Fast(attempts: 1), logger);

        var attempts = 0;
        runner.Run("Start for the foreground", _ =>
        {
            attempts++;
            return Refused();
        });

        await runner.Completion;

        Assert.Equal(1, attempts);
        Assert.Single(logger.At(LogLevel.Error));
    }
}

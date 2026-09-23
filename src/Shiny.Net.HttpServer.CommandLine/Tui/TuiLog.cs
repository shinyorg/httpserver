using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer.CommandLine.Tui;


/// <summary>
/// Catches the server's log while the dashboard owns the screen. A console logger writing underneath
/// a full-screen UI would tear it, so lines are queued here and drained into the log tab on the
/// render thread.
/// </summary>
/// <remarks>
/// The level is read on every call rather than fixed when the server is built, which is what lets the
/// verbose switch take effect without rebuilding anything.
/// </remarks>
sealed class TuiLog : ILoggerProvider
{
    const int MaxQueued = 5000;

    readonly ConcurrentQueue<LogLine> pending = new();

    /// <summary>Debug and up when true; warnings and up otherwise - the same line the console draws.</summary>
    public volatile bool Verbose;

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    /// <summary>Everything logged since the last call, oldest first. Render thread only.</summary>
    public IEnumerable<LogLine> Drain()
    {
        while (this.pending.TryDequeue(out var line))
            yield return line;
    }

    /// <summary>For the dashboard's own notes - applying settings, the tunnel moving - in the same stream.</summary>
    public void Write(LogLevel level, string category, string message)
    {
        // A runaway logger must not become a memory leak while nobody is looking at the tab.
        if (this.pending.Count >= MaxQueued)
            this.pending.TryDequeue(out _);

        this.pending.Enqueue(new LogLine(DateTimeOffset.Now, level, category, message));
    }

    bool IsEnabled(string category, LogLevel level)
    {
        if (level == LogLevel.None)
            return false;

        // The tunnel warns that it trusts an unverified host key, which is what a quick tunnel does by
        // design and what the overview already says in plainer words. Kept for verbose, as on the console.
        if (category.StartsWith("Shiny.Net.HttpServer.Ssh", StringComparison.Ordinal) && !this.Verbose)
            return level >= LogLevel.Error;

        return level >= (this.Verbose ? LogLevel.Debug : LogLevel.Warning);
    }

    public void Dispose()
    {
    }


    sealed class Logger(TuiLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => owner.IsEnabled(category, logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!this.IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            if (exception is not null)
                message = $"{message} - {exception.GetType().Name}: {exception.Message}";

            owner.Write(logLevel, category, message);
        }
    }
}


readonly record struct LogLine(DateTimeOffset At, LogLevel Level, string Category, string Message);

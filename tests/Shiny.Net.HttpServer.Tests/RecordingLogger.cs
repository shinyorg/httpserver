using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>One log entry, flattened to the three things a test cares about.</summary>
sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// A logger that keeps what it was told.
/// <para>
/// The level a failure is reported at is behaviour here rather than presentation: a crash reporter's
/// Microsoft.Extensions.Logging bridge files an event at <see cref="LogLevel.Error"/> and leaves
/// only a breadcrumb at <see cref="LogLevel.Warning"/>, so "the server gave up and said so" and "the
/// server gave up quietly" differ by exactly one argument. That is worth asserting on.
/// </para>
/// </summary>
sealed class RecordingLogger<T> : ILogger<T>
{
    readonly List<LogEntry> entries = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        // Written from whatever thread the retry loop landed on, read from the test's.
        lock (this.entries)
            this.entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }

    public IReadOnlyList<LogEntry> At(LogLevel level)
    {
        lock (this.entries)
            return [.. this.entries.Where(x => x.Level == level)];
    }
}

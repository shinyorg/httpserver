using System.Text;

namespace Shiny.Net.HttpServer.Sse;

/// <summary>
/// One message in a <c>text/event-stream</c>.
/// <para>
/// Every field is optional, which is a genuine part of the format rather than laziness: an event
/// with only <see cref="Comment"/> is a heartbeat, one with only <see cref="Retry"/> tells the
/// browser how long to wait before reconnecting, and one with only <see cref="Id"/> moves the
/// client's <c>Last-Event-ID</c> without delivering anything.
/// </para>
/// </summary>
public sealed class ServerSentEvent
{
    /// <summary>The payload. Newlines are re-emitted as separate <c>data:</c> lines, as the format requires.</summary>
    public string? Data { get; init; }

    /// <summary>Event name, dispatched to a matching <c>addEventListener</c> rather than <c>onmessage</c>.</summary>
    public string? Event { get; init; }

    /// <summary>
    /// Event id. The browser sends the last one back as <c>Last-Event-ID</c> when it reconnects,
    /// which is the whole resumption story for SSE.
    /// </summary>
    public string? Id { get; init; }

    /// <summary>How long the client should wait before reconnecting after a drop.</summary>
    public TimeSpan? Retry { get; init; }

    /// <summary>
    /// A comment line. Delivers nothing to the application but keeps the connection warm, which is
    /// what stops an idle proxy from closing it.
    /// </summary>
    public string? Comment { get; init; }

    public static ServerSentEvent Message(string data) => new() { Data = data };

    public static ServerSentEvent Named(string eventName, string data) => new() { Event = eventName, Data = data };

    /// <summary>A comment-only event, for keeping an idle stream alive.</summary>
    public static ServerSentEvent Heartbeat(string comment = "ping") => new() { Comment = comment };

    /// <summary>
    /// Writes this event in the wire format. Field order follows the spec's examples: comment,
    /// retry, id, event, then data, terminated by the blank line that dispatches it.
    /// </summary>
    internal void WriteTo(StringBuilder builder)
    {
        if (this.Comment is { } comment)
            WriteLines(builder, ":", comment);

        if (this.Retry is { } retry)
            builder.Append("retry:").Append((long)retry.TotalMilliseconds).Append('\n');

        // A newline inside an id or event name would inject a field break, so those are single-line
        // by construction — anything past the first line is dropped rather than smuggled through.
        if (this.Id is { } id)
            builder.Append("id:").Append(FirstLine(id)).Append('\n');

        if (this.Event is { } name)
            builder.Append("event:").Append(FirstLine(name)).Append('\n');

        if (this.Data is { } data)
            WriteLines(builder, "data:", data);

        builder.Append('\n');
    }

    static void WriteLines(StringBuilder builder, string prefix, string value)
    {
        // Multi-line payloads are legal and common (JSON pretty-printed, log lines); each physical
        // line becomes its own field and the client rejoins them with "\n".
        var start = 0;

        while (start <= value.Length)
        {
            var end = value.IndexOfAny(['\n', '\r'], start);
            if (end < 0)
            {
                builder.Append(prefix).Append(value, start, value.Length - start).Append('\n');
                return;
            }

            builder.Append(prefix).Append(value, start, end - start).Append('\n');

            // Treat CRLF as one break.
            start = end + (value[end] == '\r' && end + 1 < value.Length && value[end + 1] == '\n' ? 2 : 1);
        }
    }

    static string FirstLine(string value)
    {
        var end = value.IndexOfAny(['\n', '\r']);
        return end < 0 ? value : value[..end];
    }
}

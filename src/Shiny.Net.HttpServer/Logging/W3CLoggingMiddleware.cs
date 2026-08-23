using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer.Logging;

/// <summary>
/// Writes one W3C extended log line per request.
/// <para>
/// The format is the one IIS writes and every log analyser reads, which is the whole argument for
/// it: a plain text file, self-describing through its <c>#Fields</c> directive, that GoAccess,
/// AWStats, Log Parser or a spreadsheet opens without being told anything. An embedded server
/// usually has nowhere to ship structured telemetry to, and a file someone can pull off the device
/// is worth more than a log nobody collects.
/// </para>
/// <code>
/// app.UseW3CLogging(o => o.LogDirectory = FileSystem.AppDataDirectory);
/// </code>
/// <para>
/// Nothing is written on the request path — the line is formatted and queued, and a background task
/// puts it on disk.
/// </para>
/// </summary>
public sealed class W3CLoggingMiddleware : IHttpMiddleware
{
    readonly W3CLoggerOptions options;
    readonly IW3CLogWriter writer;
    readonly W3CLoggingFields[] order;

    public W3CLoggingMiddleware(W3CLoggerOptions options, IW3CLogWriter writer)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(writer);

        this.options = options;
        this.writer = writer;

        this.order = [.. Enum.GetValues<W3CLoggingFields>()
            .Where(x => x is not (W3CLoggingFields.None or W3CLoggingFields.Default or W3CLoggingFields.All))
            .Where(x => options.Fields.HasFlag(x))];

        // Declared once, up front: a reader takes its column meanings from the most recent
        // #Fields directive, so the file has to carry one before the first line.
        this.writer.SetFields(this.BuildFieldNames());
    }

    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (this.options.ShouldLog is { } predicate && !predicate(context))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var timestamp = Stopwatch.GetTimestamp();

        // Captured before the pipeline runs: a middleware below may rewrite the path, replace the
        // body or consume the query, and the log should say what the client actually asked for.
        var path = context.Request.Path;
        var query = context.Request.QueryString;
        var method = context.Request.Method;
        var received = context.Request.ContentLength;

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            this.writer.Write(this.Format(context, method, path, query, received, Stopwatch.GetElapsedTime(timestamp)));
        }
    }

    string BuildFieldNames()
    {
        var names = new List<string>(this.order.Length + this.options.AdditionalRequestHeaders.Count);

        foreach (var field in this.order)
            names.Add(NameOf(field));

        foreach (var header in this.options.AdditionalRequestHeaders)
            names.Add($"cs({header})");

        return string.Join(' ', names);
    }

    static string NameOf(W3CLoggingFields field) => field switch
    {
        W3CLoggingFields.Date => "date",
        W3CLoggingFields.Time => "time",
        W3CLoggingFields.ClientIpAddress => "c-ip",
        W3CLoggingFields.UserName => "cs-username",
        W3CLoggingFields.ServerIpAddress => "s-ip",
        W3CLoggingFields.ServerPort => "s-port",
        W3CLoggingFields.Method => "cs-method",
        W3CLoggingFields.UriStem => "cs-uri-stem",
        W3CLoggingFields.UriQuery => "cs-uri-query",
        W3CLoggingFields.ProtocolStatus => "sc-status",
        W3CLoggingFields.BytesSent => "sc-bytes",
        W3CLoggingFields.BytesReceived => "cs-bytes",
        W3CLoggingFields.TimeTaken => "time-taken",
        W3CLoggingFields.ProtocolVersion => "cs-version",
        W3CLoggingFields.Host => "cs-host",
        W3CLoggingFields.UserAgent => "cs(User-Agent)",
        W3CLoggingFields.Referer => "cs(Referer)",
        W3CLoggingFields.Cookie => "cs(Cookie)",

        // Not W3C fields. The format's own rule for an extension is the x- prefix.
        W3CLoggingFields.Route => "x-route",
        W3CLoggingFields.ConnectionId => "x-connection-id",
        _ => "x-unknown"
    };

    string Format(
        HttpContext context,
        string method,
        string path,
        string? query,
        long? received,
        TimeSpan elapsed
    )
    {
        var now = DateTimeOffset.UtcNow;
        var request = context.Request;
        var line = new StringBuilder(160);

        foreach (var field in this.order)
        {
            if (line.Length > 0)
                line.Append(' ');

            line.Append(Sanitize(field switch
            {
                W3CLoggingFields.Date => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                W3CLoggingFields.Time => now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                W3CLoggingFields.ClientIpAddress => context.Connection.RemoteIpAddress?.ToString(),
                W3CLoggingFields.UserName => context.User.Identity?.Name,
                W3CLoggingFields.ServerIpAddress => context.Connection.LocalIpAddress?.ToString(),
                W3CLoggingFields.ServerPort => context.Connection.LocalPort.ToString(CultureInfo.InvariantCulture),
                W3CLoggingFields.Method => method,
                W3CLoggingFields.UriStem => path,
                W3CLoggingFields.UriQuery => query?.TrimStart('?'),
                W3CLoggingFields.ProtocolStatus => context.Response.StatusCode.ToString(CultureInfo.InvariantCulture),
                W3CLoggingFields.BytesSent => context.Response.ContentLength?.ToString(CultureInfo.InvariantCulture),
                W3CLoggingFields.BytesReceived => received?.ToString(CultureInfo.InvariantCulture),
                W3CLoggingFields.TimeTaken => elapsed.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture),
                W3CLoggingFields.ProtocolVersion => request.Protocol,
                W3CLoggingFields.Host => request.Host,
                W3CLoggingFields.UserAgent => request.Headers.GetFirst(HeaderNames.UserAgent),
                W3CLoggingFields.Referer => request.Headers.GetFirst("Referer"),
                W3CLoggingFields.Cookie => request.Headers.GetFirst(HeaderNames.Cookie),
                W3CLoggingFields.Route => (context.Endpoint as Routing.RouteEndpoint)?.Template.RawText,
                W3CLoggingFields.ConnectionId => context.Connection.ConnectionId,
                _ => null
            }));
        }

        foreach (var header in this.options.AdditionalRequestHeaders)
        {
            line.Append(' ');
            line.Append(Sanitize(request.Headers.GetFirst(header)));
        }

        return line.ToString();
    }

    /// <summary>
    /// Fields are separated by spaces, so a value containing one would silently become two columns.
    /// The convention every W3C writer uses is to replace them with '+', and control characters go
    /// entirely — a log file is read by a terminal often enough for that to matter.
    /// </summary>
    static string Sanitize(string? value)
    {
        if (value is not { Length: > 0 })
            return "-";

        var builder = new StringBuilder(value.Length);

        foreach (var c in value)
        {
            if (char.IsControl(c))
                continue;

            builder.Append(char.IsWhiteSpace(c) ? '+' : c);
        }

        return builder.Length == 0 ? "-" : builder.ToString();
    }
}

/// <summary>Registering W3C logging.</summary>
public static class W3CLoggingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the options and the file writer.
    /// <code>
    /// builder.Services.AddW3CLogging(o =>
    /// {
    ///     o.LogDirectory = FileSystem.AppDataDirectory;
    ///     o.Fields |= W3CLoggingFields.Route;
    /// });
    /// </code>
    /// </summary>
    public static ShinyHttpServerBuilder AddW3CLogging(this ShinyHttpServerBuilder builder, Action<W3CLoggerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(_ =>
        {
            var options = new W3CLoggerOptions();
            configure?.Invoke(options);

            return options;
        });

        builder.Services.TryAddSingleton<IW3CLogWriter>(sp => new W3CLogFileWriter(
            sp.GetRequiredService<W3CLoggerOptions>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<W3CLogFileWriter>()
        ));

        return builder;
    }
}

/// <summary>Putting W3C logging in the pipeline.</summary>
public static class HttpServerW3CLoggingExtensions
{
    /// <summary>
    /// Writes a W3C extended log line per request.
    /// <para>
    /// Register it early — a line should describe the whole exchange, including the time spent in
    /// the middleware below it — but after anything that decides <em>who</em> the caller is, if
    /// <c>cs-username</c> is wanted, since that field is read when the pipeline unwinds and
    /// authentication has run by then either way.
    /// </para>
    /// <para>
    /// The writer is flushed when the server stops, so the last lines of a session reach the file
    /// rather than dying with the process.
    /// </para>
    /// </summary>
    public static HttpServer UseW3CLogging(this HttpServer server, Action<W3CLoggerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(server);

        var options = server.Services?.GetService<W3CLoggerOptions>() ?? new W3CLoggerOptions();
        configure?.Invoke(options);

        var writer = server.Services?.GetService<IW3CLogWriter>()
            ?? new W3CLogFileWriter(options, server.Services?.GetService<ILoggerFactory>()?.CreateLogger<W3CLogFileWriter>());

        return server.UseW3CLogging(options, writer);
    }

    /// <summary>Writes to a writer built elsewhere — a rolling buffer, a socket, a test double.</summary>
    public static HttpServer UseW3CLogging(this HttpServer server, W3CLoggerOptions options, IW3CLogWriter writer)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(writer);

        // Not an async lambda: an EventHandler returning void makes one async void, and anything it
        // throws — a disposed writer, a disk that went away — becomes an unhandled exception on a
        // thread pool thread, which takes the process down over a log line.
        server.StateChanged += (_, state) =>
        {
            if (state != HttpServerState.Stopped)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await writer.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The writer is already gone, or the disk is. Neither is worth a crash at
                    // shutdown, and the lines it was holding are the least important ones.
                }
            }, CancellationToken.None);
        };

        return server.Use(new W3CLoggingMiddleware(options, writer));
    }
}

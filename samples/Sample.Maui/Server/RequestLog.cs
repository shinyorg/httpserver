using System.Diagnostics;
using System.Globalization;
using Shiny.Net.HttpServer;

namespace Sample.Maui.Server;

/// <summary>One header as the screen shows it: the name, and every value for it joined up.</summary>
public sealed record HeaderLine(string Name, string Value);

/// <summary>
/// A finished request, copied out of the pipeline.
/// <para>
/// Every field is a copy rather than a reference to the <see cref="HttpContext"/> it came from.
/// Contexts are pooled and reset for the next request on the same connection, so a screen holding
/// one would show whatever request happens to be in flight when someone looks at it.
/// </para>
/// </summary>
public sealed record RequestLogEntry(
    int Id,
    DateTimeOffset StartedUtc,
    string Method,
    string Path,
    string? QueryString,
    string Protocol,
    string Scheme,
    string? Host,
    string? RemoteAddress,
    int RemotePort,
    string? UserAgent,
    string User,
    bool IsTunneled,
    bool IsEncrypted,
    IReadOnlyList<HeaderLine> RequestHeaders,
    int StatusCode,
    IReadOnlyList<HeaderLine> ResponseHeaders,
    double ElapsedMs,
    string? Error
)
{
    /// <summary>Path and query, the way it appeared on the request line.</summary>
    public string Target => this.QueryString is { Length: > 0 } query ? this.Path + query : this.Path;

    public string Summary => $"{this.Method} {this.Target}";

    public string LocalTime => this.StartedUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public string LocalDateTime => this.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public string Duration => this.ElapsedMs < 1
        ? $"{this.ElapsedMs * 1000:F0} µs"
        : $"{this.ElapsedMs:F1} ms";

    public string StatusText => this.Error is null
        ? this.StatusCode.ToString(CultureInfo.InvariantCulture)
        : "ERR";

    public string Peer => this.RemoteAddress is { Length: > 0 } address
        ? $"{address}:{this.RemotePort}"
        : "unknown";

    /// <summary>Where the connection arrived from, in the terms a person cares about.</summary>
    public string Origin => this.IsTunneled ? "Tunnel" : "This network";

    public bool HasError => this.Error is { Length: > 0 };

    /// <summary>
    /// Status colouring, kept on the entry rather than in a value converter — this is one
    /// expression and a converter would be three files.
    /// </summary>
    public Color StatusColor => this.Error is not null || this.StatusCode >= 500
        ? Color.FromArgb("#D9534F")
        : this.StatusCode is 401 or 403
            ? Color.FromArgb("#E0A030")
            : this.StatusCode >= 400
                ? Color.FromArgb("#E07B39")
                : this.StatusCode >= 300
                    ? Color.FromArgb("#4A8FE7")
                    : Color.FromArgb("#3FA45B");
}

/// <summary>
/// Records every request the server answers, so the app can show its own traffic.
/// <para>
/// This is all an <see cref="IHttpMiddleware"/> is: something wrapped around the rest of the
/// pipeline. It goes in first, ahead of authentication, so a caller who fails the password prompt
/// still shows up on screen — which is usually the request you most want to see.
/// </para>
/// </summary>
public sealed class RequestLog : IHttpMiddleware
{
    /// <summary>How many requests are kept. A phone is not a log server.</summary>
    const int Capacity = 200;

    /// <summary>
    /// Headers whose values are credentials. The device owner already knows their own password, but
    /// a screenshot of this screen should not be a way to hand it over.
    /// </summary>
    static readonly string[] Redacted =
    [
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Set-Cookie"
    ];

    readonly object sync = new();
    readonly Queue<RequestLogEntry> entries = new(Capacity);
    int total;

    /// <summary>Requests since the app started — which is not the same as how many are kept.</summary>
    public int Total => Volatile.Read(ref this.total);

    /// <summary>Raised as each request finishes. Fires off the UI thread; marshal before binding.</summary>
    public event EventHandler<RequestLogEntry>? Added;

    public event EventHandler? Cleared;

    /// <summary>Newest first, which is the order the screen wants.</summary>
    public RequestLogEntry[] Snapshot()
    {
        lock (this.sync)
            return [.. this.entries.Reverse()];
    }

    public RequestLogEntry? Find(int id)
    {
        lock (this.sync)
            return this.entries.FirstOrDefault(x => x.Id == id);
    }

    public void Clear()
    {
        lock (this.sync)
            this.entries.Clear();

        this.Cleared?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var id = Interlocked.Increment(ref this.total);
        var started = DateTimeOffset.UtcNow;
        var timestamp = Stopwatch.GetTimestamp();

        // Copied here, before anything downstream can run: a handler is free to rewrite the path,
        // and the headers are recycled with the context the moment this request is done with.
        var request = context.Request;
        var method = request.Method;
        var path = request.Path;
        var query = request.QueryString;
        var protocol = request.Protocol;
        var scheme = request.Scheme;
        var host = request.Host;
        var userAgent = request.Headers.GetFirst(HeaderNames.UserAgent);
        var requestHeaders = SnapshotHeaders(request.Headers);
        var remote = context.Connection.RemoteIpAddress?.ToString();
        var remotePort = context.Connection.RemotePort;
        var tunneled = context.Connection.IsTunneled;
        var encrypted = context.Connection.IsEncrypted;

        string? error = null;

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Recorded and rethrown: the pipeline's own handler owns the response, this only owns
            // the note that says why the entry reads 500.
            error = ex.Message;
            throw;
        }
        finally
        {
            // The user is read on the way out rather than on the way in — authentication runs below
            // this middleware, so on the way in nobody has been identified yet.
            var entry = new RequestLogEntry(
                id,
                started,
                method,
                path,
                query,
                protocol,
                scheme,
                host,
                remote,
                remotePort,
                userAgent,
                context.User.Identity?.Name ?? "anonymous",
                tunneled,
                encrypted,
                requestHeaders,
                context.Response.StatusCode,
                SnapshotHeaders(context.Response.Headers),
                Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds,
                error
            );

            this.Add(entry);
        }
    }

    void Add(RequestLogEntry entry)
    {
        lock (this.sync)
        {
            this.entries.Enqueue(entry);

            if (this.entries.Count > Capacity)
                this.entries.Dequeue();
        }

        // Outside the lock: a handler on the other end runs UI work, and holding a lock across that
        // is how a list control ends up deadlocked against a request thread.
        this.Added?.Invoke(this, entry);
    }

    static HeaderLine[] SnapshotHeaders(HeaderDictionary headers) =>
    [
        .. headers
            .Select(header => new HeaderLine(
                header.Key,
                Redacted.Contains(header.Key, StringComparer.OrdinalIgnoreCase)
                    ? "«redacted»"
                    // StringValues joins its values with a comma, which is what a repeated header
                    // means on the wire anyway.
                    : header.Value.ToString()
            ))
            .OrderBy(header => header.Name, StringComparer.OrdinalIgnoreCase)
    ];
}

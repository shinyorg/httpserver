using System.Globalization;

namespace Shiny.Net.HttpServer.CommandLine.Monitoring;


/// <summary>
/// One request and its response, as the dashboard sees it. Built when the request arrives and filled
/// in while it runs, so a transfer can be watched as it happens rather than read about afterwards.
/// </summary>
/// <remarks>
/// Nothing here holds on to the <see cref="HttpContext"/>. Contexts are pooled, and an entry outlives
/// its request by as long as it stays in the log - so everything worth showing is copied out while
/// the request still owns it.
/// </remarks>
public sealed class TrafficEntry
{
    long bytesIn;
    long bytesOut;
    long responseLength = -1;
    long completedTicks;

    internal TrafficEntry(long id, HttpContext context)
    {
        var request = context.Request;

        this.Id = id;
        this.StartedAt = DateTimeOffset.Now;
        this.StartedTimestamp = Environment.TickCount64;
        this.Method = request.Method;
        this.Target = request.QueryString is { Length: > 0 } query ? request.Path + EnsureQuestionMark(query) : request.Path;
        this.Protocol = request.Protocol;
        this.IsTunneled = context.Connection.IsTunneled;
        this.RemoteAddress = ClientAddress(context);
        this.IsEncrypted = context.Connection.IsEncrypted;
        this.RequestLength = request.ContentLength;
        this.RequestHeaders = Copy(request.Headers);
    }

    public long Id { get; }
    public DateTimeOffset StartedAt { get; }
    internal long StartedTimestamp { get; }

    public string Method { get; }

    /// <summary>Path and query, as the client asked for it.</summary>
    public string Target { get; }

    public string Protocol { get; }
    public string RemoteAddress { get; }

    /// <summary>Came in through the public tunnel rather than a local socket.</summary>
    public bool IsTunneled { get; }

    public bool IsEncrypted { get; }

    /// <summary>What the client said it would send. Null for a chunked or bodiless request.</summary>
    public long? RequestLength { get; }

    /// <summary>What the response said it would send, once its headers have gone out.</summary>
    public long? ResponseLength
    {
        get
        {
            var value = Interlocked.Read(ref this.responseLength);
            return value < 0 ? null : value;
        }
    }

    public IReadOnlyList<KeyValuePair<string, string>> RequestHeaders { get; }
    public IReadOnlyList<KeyValuePair<string, string>> ResponseHeaders { get; private set; } = [];

    /// <summary>Request body bytes the server has read so far.</summary>
    public long BytesIn => Interlocked.Read(ref this.bytesIn);

    /// <summary>Response body bytes the handler has written so far.</summary>
    public long BytesOut => Interlocked.Read(ref this.bytesOut);

    public int StatusCode { get; private set; }
    public string? ContentType { get; private set; }

    /// <summary>Who the request authenticated as, when anyone.</summary>
    public string? User { get; private set; }

    /// <summary>Why the handler failed, when it threw.</summary>
    public string? Error { get; private set; }

    public bool IsComplete => Volatile.Read(ref this.completedTicks) != 0;

    /// <summary>How long it took, or how long it has been running.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            var end = Volatile.Read(ref this.completedTicks);
            return TimeSpan.FromMilliseconds((end == 0 ? Environment.TickCount64 : end) - this.StartedTimestamp);
        }
    }

    /// <summary>A body going up: something the client is sending to be stored.</summary>
    public bool IsUpload => this.RequestLength > 0 || this.BytesIn > 0;

    /// <summary>
    /// A body coming down that is a file rather than a page about files. The mount answers a
    /// directory with its HTML manager and PROPFIND with XML; neither is what "a download" means to
    /// the person watching one.
    /// </summary>
    public bool IsDownload
        => this.Method is "GET"
           && (this.ResponseLength > 0 || this.BytesOut > 0)
           && this.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) != true;

    /// <summary>The size a progress bar measures against, when either side announced one.</summary>
    public long? ExpectedBytes => this.IsUpload ? this.RequestLength : this.ResponseLength;

    public long TransferredBytes => this.IsUpload ? this.BytesIn : this.BytesOut;

    /// <summary>0..1 when the size is known, otherwise null - a bar that guesses is worse than none.</summary>
    public double? Progress
        => this.ExpectedBytes is > 0 and var expected
            ? Math.Clamp(this.TransferredBytes / (double)expected, 0, 1)
            : null;


    internal void AddBytesIn(long count) => Interlocked.Add(ref this.bytesIn, count);
    internal void AddBytesOut(long count) => Interlocked.Add(ref this.bytesOut, count);

    internal void SetResponseLength(long? length)
    {
        if (length is { } value)
            Interlocked.Exchange(ref this.responseLength, value);
    }

    /// <summary>
    /// The content type is known the moment headers go out, and a download is told apart from a
    /// listing by it - so it is taken then, not at the end, or an in-flight file would not count.
    /// </summary>
    internal void SetContentType(string? contentType) => this.ContentType = contentType;


    internal void Complete(HttpContext context, Exception? error)
    {
        var response = context.Response;

        // A handler that threw before writing anything is answered by the server with a 500, after
        // this has run - so that is what the client got, whatever the status field says right now.
        this.StatusCode = error is not null && !response.HasStarted ? StatusCodes.Status500InternalServerError : response.StatusCode;
        this.ContentType ??= response.ContentType;
        this.SetResponseLength(response.ContentLength);
        this.ResponseHeaders = Copy(response.Headers);
        this.User = context.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : null;
        this.Error = error?.Message;

        Volatile.Write(ref this.completedTicks, Math.Max(Environment.TickCount64, this.StartedTimestamp + 1));
    }


    /// <summary>
    /// Who is on the other end. Through the tunnel the socket is the tunnel's own forward, which is
    /// always this machine - so there, and only there, the forwarding header the tunnel adds is the
    /// answer. On a direct connection that header is whatever the client chose to write, and the
    /// socket is believed instead.
    /// <para>
    /// The last entry, not the first: a proxy appends the address it saw, so everything before that
    /// is what the client sent - and a client can send anything.
    /// </para>
    /// </summary>
    static string ClientAddress(HttpContext context)
    {
        if (context.Connection.IsTunneled && context.Request.Headers["X-Forwarded-For"].ToString() is { Length: > 0 } forwarded)
        {
            var last = forwarded[(forwarded.LastIndexOf(',') + 1)..].Trim();
            if (last.Length > 0)
                return last;
        }
        return context.Connection.RemoteIpAddress?.ToString() ?? "";
    }


    static string EnsureQuestionMark(string query) => query.StartsWith('?') ? query : "?" + query;


    static KeyValuePair<string, string>[] Copy(HeaderDictionary headers)
    {
        var list = new List<KeyValuePair<string, string>>(headers.Count);
        foreach (var header in headers)
            list.Add(new(header.Key, header.Value.ToString()));

        return list.ToArray();
    }


    public override string ToString()
        => String.Create(CultureInfo.InvariantCulture, $"{this.Method} {this.Target} {this.StatusCode}");
}

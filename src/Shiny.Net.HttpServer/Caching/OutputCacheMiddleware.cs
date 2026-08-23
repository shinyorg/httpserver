using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;

namespace Shiny.Net.HttpServer.Caching;

/// <summary>
/// Serves a stored response instead of running the handler again.
/// <para>
/// Worth having on a device for a reason a datacentre cache is not: the expensive part is rarely
/// the network, it is the work behind the endpoint — a database read, a sensor poll, a JSON
/// serialisation — and every one of those costs battery. A cached list view is the difference
/// between a screen that redraws and a screen that spins.
/// </para>
/// <para>
/// Runs after routing, because what to cache is a property of the endpoint.
/// </para>
/// </summary>
public sealed class OutputCacheMiddleware(OutputCacheOptions options, IOutputCacheStore store) : IHttpMiddleware
{
    /// <summary>Separates the parts of a cache key. Not a byte any of them can contain.</summary>
    const char KeySeparator = '\u001f';

    readonly OutputCacheOptions options = options ?? throw new ArgumentNullException(nameof(options));
    readonly IOutputCacheStore store = store ?? throw new ArgumentNullException(nameof(store));

    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var policy = this.PolicyFor(context.Endpoint?.GetMetadata<OutputCacheMetadata>());

        if (policy is null || !IsCacheableRequest(context, policy))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var key = BuildKey(context, policy);

        if (await this.store.GetAsync(key, context.RequestAborted).ConfigureAwait(false) is { } hit)
        {
            await ServeAsync(context, hit).ConfigureAwait(false);
            return;
        }

        var buffer = new BufferingBodyControl(context.Response, this.options.MaxBodyBytes);
        context.Response.Bind(buffer);

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            // Flushed here rather than through CompleteAsync: the connection completes its own
            // producer, not whatever the response ended up bound to, so anything still buffered
            // when this unwinds would never reach the wire.
            await buffer.FinishAsync(context.RequestAborted).ConfigureAwait(false);
        }

        if (buffer.Captured is { } body && this.ShouldStore(context, policy))
        {
            var now = DateTimeOffset.UtcNow;
            var entry = new OutputCacheEntry(
                context.Response.StatusCode,
                Storable(context.Response.Headers),
                body,
                now,
                now + policy.Duration
            );

            await this.store.SetAsync(key, entry, CancellationToken.None).ConfigureAwait(false);
        }
    }

    OutputCachePolicy? PolicyFor(OutputCacheMetadata? metadata)
    {
        if (metadata is null)
            return this.options.DefaultPolicy;

        if (metadata.Disabled)
            return null;

        if (metadata.Duration is { } duration)
            return new OutputCachePolicy(duration);

        if (metadata.PolicyName is { } name)
            return this.options.GetPolicy(name);

        return this.options.DefaultPolicy;
    }

    static bool IsCacheableRequest(HttpContext context, OutputCachePolicy policy)
    {
        var request = context.Request;

        // Only the methods that are supposed to have no side effects. Caching a POST is how a
        // "submit" button starts returning yesterday's confirmation.
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
            return false;

        if (!policy.AllowAuthenticated
            && (request.Headers.ContainsKey(HeaderNames.Authorization) || context.User.Identity?.IsAuthenticated == true))
        {
            return false;
        }

        // A client that explicitly asked for a fresh copy gets one.
        var cacheControl = request.Headers.GetFirst(HeaderNames.CacheControl);

        return cacheControl is null
            || (!cacheControl.Contains("no-cache", StringComparison.OrdinalIgnoreCase)
                && !cacheControl.Contains("no-store", StringComparison.OrdinalIgnoreCase));
    }

    bool ShouldStore(HttpContext context, OutputCachePolicy policy)
    {
        var response = context.Response;

        if (response.StatusCode != StatusCodes.Status200OK)
            return false;

        // Set-Cookie is per caller by definition. Storing one means handing the next caller a
        // session that is not theirs.
        if (response.Headers.ContainsKey(HeaderNames.SetCookie))
            return false;

        if (response.Headers.GetFirst(HeaderNames.CacheControl) is { } cacheControl
            && cacheControl.Contains("no-store", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return policy.ShouldCache is not { } predicate || predicate(context);
    }

    static string BuildKey(HttpContext context, OutputCachePolicy policy)
    {
        var request = context.Request;
        var key = new StringBuilder(request.Method).Append(KeySeparator).Append(request.Path);

        if (policy.VaryByQueryKeys.Count > 0)
        {
            foreach (var name in policy.VaryByQueryKeys)
                key.Append(KeySeparator).Append(name).Append('=').Append(request.Query[name].ToString());
        }
        else if (policy.VaryByQuery)
        {
            key.Append(KeySeparator).Append(request.QueryString);
        }

        foreach (var name in policy.VaryByHeaders)
            key.Append(KeySeparator).Append(name).Append(':').Append(request.Headers.GetFirst(name));

        return key.ToString();
    }

    /// <summary>
    /// Everything except the headers that describe this connection rather than this response.
    /// Replaying a stored <c>Transfer-Encoding</c> or <c>Connection</c> would frame the reply for a
    /// connection that no longer exists.
    /// </summary>
    static List<KeyValuePair<string, string>> Storable(HeaderDictionary headers)
    {
        var stored = new List<KeyValuePair<string, string>>(headers.Count);

        foreach (var header in headers)
        {
            if (IsHopByHop(header.Key))
                continue;

            for (var i = 0; i < header.Value.Count; i++)
            {
                if (header.Value[i] is { } value)
                    stored.Add(new KeyValuePair<string, string>(header.Key, value));
            }
        }

        return stored;
    }

    static bool IsHopByHop(string name)
        => string.Equals(name, HeaderNames.Connection, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, HeaderNames.TransferEncoding, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, HeaderNames.KeepAlive, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, HeaderNames.Date, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, HeaderNames.ContentLength, StringComparison.OrdinalIgnoreCase);

    static async ValueTask ServeAsync(HttpContext context, OutputCacheEntry entry)
    {
        var response = context.Response;

        foreach (var header in entry.Headers)
            response.Headers.Append(header.Key, header.Value);

        var age = (long)Math.Max(0, (DateTimeOffset.UtcNow - entry.Created).TotalSeconds);
        response.Headers.Set("Age", age.ToString(CultureInfo.InvariantCulture));

        // A client holding this version gets the 304 it asked for straight from the entry — the
        // whole exchange is then a couple of hundred bytes with no handler and no body.
        if (context.CheckPreconditions(entry.ETag) == PreconditionResult.NotModified)
        {
            await context.CompletePreconditionAsync(PreconditionResult.NotModified).ConfigureAwait(false);
            return;
        }

        response.StatusCode = entry.StatusCode;
        response.ContentLength = entry.Body.Length;

        if (HttpMethods.IsHead(context.Request.Method))
        {
            await response.StartAsync(context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await response.WriteBytesAsync(entry.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
    }
}

/// <summary>
/// Holds the response body in memory so it can be stored, then writes it on.
/// <para>
/// Buffering has a second benefit worth the memory: a response whose length was unknown becomes one
/// with a Content-Length, so it is not chunked. It also has a hard limit — past it, everything
/// buffered is flushed, the entry is abandoned, and the rest streams through untouched.
/// </para>
/// </summary>
sealed class BufferingBodyControl : IResponseBodyControl
{
    readonly HttpResponse response;
    readonly IResponseBodyControl inner;
    readonly int maxBytes;

    ArrayBufferWriter<byte>? buffer = new(1024);
    BufferStream? stream;
    PipeWriter? writer;
    bool passThrough;
    bool finished;

    public BufferingBodyControl(HttpResponse response, int maxBytes)
    {
        this.response = response;
        this.inner = response.BodyControl;
        this.maxBytes = maxBytes;
    }

    /// <summary>The complete body, or null when it was never captured in full.</summary>
    public byte[]? Captured { get; private set; }

    public bool HasStarted => this.inner.HasStarted;

    public Stream Stream => this.passThrough ? this.inner.Stream : this.stream ??= new BufferStream(this);

    public PipeWriter Writer => this.passThrough
        ? this.inner.Writer
        : this.writer ??= PipeWriter.Create(this.Stream, new StreamPipeWriterOptions(leaveOpen: true));

    /// <summary>
    /// A handler that flushes its headers deliberately is streaming — an event stream, a long
    /// download — and buffering one is indistinguishable from hanging. Give up on caching and get
    /// out of the way.
    /// </summary>
    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        await this.SpillAsync(cancellationToken).ConfigureAwait(false);
        await this.inner.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken) => this.inner.CompleteAsync(cancellationToken);

    /// <summary>Writes whatever was buffered through to the connection.</summary>
    public async ValueTask FinishAsync(CancellationToken cancellationToken)
    {
        if (this.finished)
            return;

        this.finished = true;

        if (this.buffer is not { } captured)
            return;

        this.buffer = null;
        this.Captured = captured.WrittenSpan.ToArray();

        if (captured.WrittenCount == 0)
            return;

        // The length is known now even when the handler never set one, so this response goes out
        // with a Content-Length instead of chunked.
        if (!this.inner.HasStarted)
            this.response.ContentLength ??= captured.WrittenCount;

        await this.inner.Stream.WriteAsync(captured.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    async ValueTask SpillAsync(CancellationToken cancellationToken)
    {
        if (this.passThrough)
            return;

        this.passThrough = true;

        if (this.buffer is not { } captured)
            return;

        this.buffer = null;

        if (captured.WrittenCount > 0)
            await this.inner.Stream.WriteAsync(captured.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (this.passThrough)
        {
            await this.inner.Stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            return;
        }

        var captured = this.buffer!;

        if (captured.WrittenCount + data.Length > this.maxBytes)
        {
            // Too big to store. Everything buffered goes out now and the rest streams.
            await this.SpillAsync(cancellationToken).ConfigureAwait(false);
            await this.inner.Stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            return;
        }

        captured.Write(data.Span);
    }

    sealed class BufferStream(BufferingBodyControl owner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
            => this.Write(new ReadOnlySpan<byte>(buffer, offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var copy = buffer.ToArray();
            owner.WriteAsync(copy, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => owner.WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => owner.WriteAsync(buffer, cancellationToken);

        public override void Flush() { }

        // Deliberately not forwarded: a flush from a buffering body would start the response and
        // defeat the buffering. Anything that genuinely needs the bytes out calls StartAsync.
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

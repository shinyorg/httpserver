using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.Net.HttpServer.Compression;

/// <summary>One content coding the server can read on the way in.</summary>
public interface IDecompressionProvider
{
    /// <summary>The token as it appears in <c>Content-Encoding</c>.</summary>
    string EncodingName { get; }

    /// <summary>Wraps the request body. The returned stream is read by the handler and never seeks.</summary>
    Stream CreateStream(Stream input);
}

/// <summary>Reads a brotli request body.</summary>
public sealed class BrotliDecompressionProvider : IDecompressionProvider
{
    public string EncodingName => "br";

    public Stream CreateStream(Stream input) => new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true);
}

/// <summary>Reads a gzip request body.</summary>
public sealed class GzipDecompressionProvider : IDecompressionProvider
{
    public string EncodingName => "gzip";

    public Stream CreateStream(Stream input) => new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
}

/// <summary>Reads a zlib/deflate request body.</summary>
public sealed class DeflateDecompressionProvider : IDecompressionProvider
{
    public string EncodingName => "deflate";

    public Stream CreateStream(Stream input) => new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true);
}

/// <summary>What the server will accept compressed, and how far it will let it expand.</summary>
public sealed class RequestDecompressionOptions
{
    /// <summary>Codings the server can read. Brotli, gzip and deflate by default.</summary>
    public IList<IDecompressionProvider> Providers { get; } =
    [
        new BrotliDecompressionProvider(),
        new GzipDecompressionProvider(),
        new DeflateDecompressionProvider()
    ];

    /// <summary>
    /// The most a decompressed body may expand to before the request is rejected with a 413.
    /// <para>
    /// This is the whole reason inbound decompression needs a switch: a few hundred kilobytes of
    /// gzip expands to gigabytes if it was built to, and a phone has neither the memory nor the
    /// battery to find out. Defaults to <see cref="HttpServerLimits.MaxRequestBodySize"/>, which
    /// otherwise only bounds the compressed bytes on the wire.
    /// </para>
    /// </summary>
    public long? MaxDecompressedBytes { get; set; }

    /// <summary>
    /// What to do with a coding no provider handles. Passing it through leaves the body as the
    /// client sent it and lets the handler decide; rejecting answers 415.
    /// </summary>
    public bool RejectUnsupportedEncodings { get; set; } = true;

    internal IDecompressionProvider? Find(string? encoding)
    {
        if (encoding is not { Length: > 0 })
            return null;

        foreach (var provider in this.Providers)
        {
            if (string.Equals(provider.EncodingName, encoding, StringComparison.OrdinalIgnoreCase))
                return provider;
        }

        return null;
    }
}

/// <summary>
/// Decompresses request bodies the client marked with <c>Content-Encoding</c>.
/// <para>
/// The mirror of <see cref="ResponseCompressionMiddleware"/>, and on a device the more valuable
/// direction: uplink is the slow, expensive, battery-hungry half of a mobile connection, and a
/// sync client posting a batch of JSON is exactly the shape that compresses well.
/// </para>
/// <code>
/// app.UseRequestDecompression();
/// </code>
/// </summary>
public sealed class RequestDecompressionMiddleware(RequestDecompressionOptions options, HttpServerLimits? limits = null) : IHttpMiddleware
{
    readonly RequestDecompressionOptions options = options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var encoding = context.Request.Headers.GetFirst(HeaderNames.ContentEncoding);

        if (encoding is not { Length: > 0 })
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // A stack of codings ("gzip, br") is legal and vanishingly rare, and unwinding one costs a
        // second buffer for no benefit anyone has asked for. Say so rather than half-handling it.
        if (encoding.Contains(',', StringComparison.Ordinal))
        {
            await this.RejectAsync(context, encoding).ConfigureAwait(false);
            return;
        }

        var provider = this.options.Find(encoding.Trim());
        if (provider is null)
        {
            if (this.options.RejectUnsupportedEncodings)
            {
                await this.RejectAsync(context, encoding).ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
            return;
        }

        var limit = this.options.MaxDecompressedBytes ?? limits?.MaxRequestBodySize;
        var decompressed = provider.CreateStream(context.Request.Body);

        context.Request.Body = limit is { } max ? new BoundedReadStream(decompressed, max) : decompressed;

        // The header describes bytes that are no longer there. Leaving either in place would have
        // a handler read a Content-Length that does not match what it can read, which is worse
        // than not knowing the length at all.
        context.Request.Headers.Remove(HeaderNames.ContentEncoding);
        context.Request.Headers.Remove(HeaderNames.ContentLength);
        context.Request.BodyDecoded = true;

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            await decompressed.DisposeAsync().ConfigureAwait(false);
        }
    }

    async ValueTask RejectAsync(HttpContext context, string encoding)
    {
        context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
        context.Response.Headers.Set("Accept-Encoding", string.Join(", ", this.options.Providers.Select(x => x.EncodingName)));

        await context.Response
            .WriteTextAsync($"Content-Encoding '{encoding}' is not supported.", cancellationToken: context.RequestAborted)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// A read stream that refuses to hand out more than it was told to. What stands between a 200KB
/// upload and the 10GB it decompresses to.
/// </summary>
sealed class BoundedReadStream(Stream inner, long limit) : Stream
{
    long read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => this.read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => this.Count(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => this.Count(inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => this.Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => this.Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

    int Count(int count)
    {
        this.read += count;

        if (this.read > limit)
            throw new BadHttpRequestException(
                $"The decompressed request body exceeded {limit} bytes.",
                StatusCodes.Status413PayloadTooLarge
            );

        return count;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Wiring request decompression into a server.</summary>
public static class RequestDecompressionExtensions
{
    /// <summary>Registers decompression options.</summary>
    public static ShinyHttpServerBuilder AddRequestDecompression(
        this ShinyHttpServerBuilder builder,
        Action<RequestDecompressionOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(_ =>
        {
            var options = new RequestDecompressionOptions();
            configure?.Invoke(options);

            return options;
        });

        return builder;
    }

    /// <summary>
    /// Decompresses request bodies. Put it before anything that reads one — model binding, the
    /// multipart reader, a body-logging middleware that would otherwise record compressed bytes.
    /// </summary>
    public static HttpServer UseRequestDecompression(
        this HttpServer server,
        Action<RequestDecompressionOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(server);

        var options = server.Services?.GetService<RequestDecompressionOptions>() ?? new RequestDecompressionOptions();
        configure?.Invoke(options);

        return server.Use(new RequestDecompressionMiddleware(options, server.Options.Limits));
    }
}

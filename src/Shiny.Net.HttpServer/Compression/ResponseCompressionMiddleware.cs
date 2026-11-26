using Microsoft.Extensions.DependencyInjection;

namespace Shiny.Net.HttpServer.Compression;

/// <summary>
/// Compresses responses the client said it could decode.
/// <para>
/// Worth more here than on a server in a datacentre: an embedded server is usually reached over
/// cellular or a tunnel, where a few hundred kilobytes of JSON is the difference between instant
/// and sluggish, and the CPU it costs is idle anyway.
/// </para>
/// <code>
/// app.UseResponseCompression();
/// </code>
/// </summary>
public sealed class ResponseCompressionMiddleware(ResponseCompressionOptions options) : IHttpMiddleware
{
    readonly ResponseCompressionOptions options = options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!this.CanCompress(context))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var provider = this.options.SelectProvider(context.Request.Headers.GetFirst(HeaderNames.AcceptEncoding));
        if (provider is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // A compressed response carries a suffixed entity tag ("abc-gzip"), so that is what a client
        // revalidating it sends back. The handler only knows the identity tag, so the suffix comes
        // off here, before anything downstream evaluates the condition.
        var heldEncodedTag = StripEncodingSuffix(context.Request, provider.EncodingName);

        var wrapper = new CompressingBodyControl(context.Response.BodyControl, context.Response, this.options, provider);

        // Deferred to the last moment before headers go out, because a handler that sets its own
        // Vary would otherwise overwrite this one. Applied whether or not this particular response
        // ends up compressed: a shared cache that stored the uncompressed copy without Vary would
        // serve it to a client that only asked for compressed, and the other way round.
        context.Response.OnStarting(() =>
        {
            AppendVary(context.Response);

            // A 304 confirms the representation the client holds, so it names that one's tag.
            if (heldEncodedTag && wrapper.AppliedEncoding is null && context.Response.StatusCode == StatusCodes.Status304NotModified)
                CompressingBodyControl.ApplyEncodingToETag(context.Response, provider.EncodingName);

            return default;
        });

        context.Response.Bind(wrapper);

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            // The connection completes its own producer, not whatever the response is bound to, so
            // the final compressed block has to be flushed here or every response is truncated.
            await wrapper.FinishAsync().ConfigureAwait(false);
        }
    }

    bool CanCompress(HttpContext context)
    {
        if (this.options.ShouldCompress is { } predicate)
            return predicate(context);

        // BREACH needs a response containing both a secret and attacker-controlled input. Rare, but
        // the only way to rule it out is to know the app — hence the switch.
        if (!this.options.EnableForHttps && context.Request.IsHttps)
            return false;

        // A HEAD carries no body, and a CONNECT or an upgrade is not ours to touch.
        return !HttpMethods.IsHead(context.Request.Method);
    }

    /// <summary>
    /// Takes <c>-{encoding}</c> off every tag in <c>If-None-Match</c>, returning whether there was
    /// one. Only that header: <c>If-Range</c> is left alone, because a range is always served
    /// uncompressed and a tag for the compressed bytes must not validate one, and <c>If-Match</c>
    /// guards writes, which this middleware never compresses the answer to anyway.
    /// </summary>
    static bool StripEncodingSuffix(HttpRequest request, string encoding)
    {
        if (request.Headers.GetFirst(HeaderNames.IfNoneMatch) is not { Length: > 0 } header)
            return false;

        var suffix = "-" + encoding + "\"";
        var tags = header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var stripped = false;

        for (var i = 0; i < tags.Length; i++)
        {
            if (tags[i].EndsWith(suffix, StringComparison.Ordinal))
            {
                tags[i] = tags[i][..^suffix.Length] + "\"";
                stripped = true;
            }
        }

        if (stripped)
            request.Headers[HeaderNames.IfNoneMatch] = string.Join(", ", tags);

        return stripped;
    }

    static void AppendVary(HttpResponse response)
    {
        var existing = response.Headers.GetFirst(HeaderNames.Vary);

        if (existing is null)
        {
            response.Headers[HeaderNames.Vary] = HeaderNames.AcceptEncoding;
            return;
        }

        if (existing.Contains(HeaderNames.AcceptEncoding, StringComparison.OrdinalIgnoreCase) || existing.Trim() == "*")
            return;

        response.Headers[HeaderNames.Vary] = existing + ", " + HeaderNames.AcceptEncoding;
    }
}

/// <summary>Wiring response compression into a server.</summary>
public static class ResponseCompressionExtensions
{
    /// <summary>
    /// Registers compression options.
    /// <code>
    /// builder.Services.AddResponseCompression(o => o.Level = CompressionLevel.Optimal);
    /// </code>
    /// </summary>
    public static ShinyHttpServerBuilder AddResponseCompression(
        this ShinyHttpServerBuilder builder,
        Action<ResponseCompressionOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton(_ =>
        {
            var options = new ResponseCompressionOptions();
            configure?.Invoke(options);

            return options;
        });

        return builder;
    }

    /// <summary>
    /// Compresses responses. Put it early — it has to wrap everything that writes a body, including
    /// static files.
    /// </summary>
    public static HttpServer UseResponseCompression(
        this HttpServer server,
        Action<ResponseCompressionOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(server);

        var options = server.Services?.GetService<ResponseCompressionOptions>() ?? new ResponseCompressionOptions();
        configure?.Invoke(options);

        return server.Use(new ResponseCompressionMiddleware(options));
    }

    /// <summary>Compresses responses using options built elsewhere.</summary>
    public static HttpServer UseResponseCompression(this HttpServer server, ResponseCompressionOptions options)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);

        return server.Use(new ResponseCompressionMiddleware(options));
    }
}

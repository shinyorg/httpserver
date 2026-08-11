using Microsoft.Extensions.DependencyInjection;

namespace Shiny.Net.HttpServer.Negotiation;

/// <summary>Which representations the server can produce and consume, and what to do when none fit.</summary>
public sealed class ContentNegotiationOptions
{
    /// <summary>
    /// Formatters, tried by the client's preference and then by their own.
    /// <para>
    /// JSON and plain text are registered by default. Removing JSON is a way to make most endpoints
    /// stop working, so it is there unless you take it out deliberately.
    /// </para>
    /// </summary>
    public IList<IOutputFormatter> Formatters { get; } = [new JsonOutputFormatter(), new PlainTextOutputFormatter()];

    /// <summary>
    /// Formatters for request bodies, selected by <c>Content-Type</c> rather than by preference —
    /// the client is stating a fact about what it sent, not expressing a wish.
    /// <para>
    /// JSON is registered by default and also claims bodies that arrive with no <c>Content-Type</c>.
    /// </para>
    /// </summary>
    public IList<IInputFormatter> InputFormatters { get; } = [new JsonInputFormatter()];

    /// <summary>
    /// Answers 406 when the client accepts nothing the server can produce.
    /// <para>
    /// On by default, because the alternative is sending a body in a format the caller told you it
    /// could not read — which fails later, further away, and less clearly. Turn it off to fall back
    /// to the highest-priority formatter instead.
    /// </para>
    /// </summary>
    public bool ReturnNotAcceptable { get; set; } = true;

    /// <summary>
    /// Makes <c>Results.Ok(value)</c> — and therefore every generated endpoint that returns a value
    /// — negotiate its representation instead of always writing JSON.
    /// <para>
    /// Off by default, and the default is the interesting part: turning it on means an endpoint's
    /// response format depends on a request header, so a client that starts sending
    /// <c>Accept: text/plain</c> silently changes what it gets. That is exactly what you want for an
    /// API serving browsers, scripts and clients alike, and exactly what you do not want for one
    /// whose contract says JSON. <c>Results.Json(...)</c> is unaffected either way — the name says
    /// JSON, so it writes JSON.
    /// </para>
    /// </summary>
    public bool NegotiateByDefault { get; set; }

    /// <summary>Adds a formatter built from a delegate.</summary>
    public ContentNegotiationOptions AddFormatter(
        string mediaType,
        Func<HttpContext, object?, CancellationToken, ValueTask> write,
        int priority = 50,
        Func<object?, bool>? canWrite = null
    )
    {
        this.Formatters.Add(new DelegateOutputFormatter(mediaType, write, priority, canWrite));
        return this;
    }

    /// <summary>Adds an input formatter built from a delegate.</summary>
    public ContentNegotiationOptions AddInputFormatter(
        string mediaType,
        Func<HttpContext, Type, CancellationToken, ValueTask<InputFormatterResult>> read,
        int priority = 50,
        Func<Type, bool>? canRead = null
    )
    {
        this.InputFormatters.Add(new DelegateInputFormatter(mediaType, read, priority, canRead));
        return this;
    }

    /// <summary>Registers a formatter for both directions.</summary>
    public ContentNegotiationOptions Add(IOutputFormatter output, IInputFormatter input)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(input);

        this.Formatters.Add(output);
        this.InputFormatters.Add(input);

        return this;
    }

    /// <summary>
    /// Picks the formatter for a request, or null when nothing the client accepts can be produced.
    /// </summary>
    public IOutputFormatter? Select(HttpRequest request, object? value)
    {
        ArgumentNullException.ThrowIfNull(request);

        var accepted = MediaTypeRange.ParseAccept(request.Headers.GetFirst(HeaderNames.Accept));

        // Ranges arrive sorted by quality then specificity, so the first range with a formatter
        // that can write this value is the answer.
        foreach (var range in accepted)
        {
            if (range.Quality <= 0)
                continue;

            IOutputFormatter? best = null;

            foreach (var formatter in this.Formatters)
            {
                if (!range.Matches(formatter.MediaType) || !formatter.CanWrite(value))
                    continue;

                if (best is null || formatter.Priority > best.Priority)
                    best = formatter;
            }

            if (best is not null)
                return best;
        }

        return null;
    }

    internal IOutputFormatter? Fallback(object? value)
    {
        IOutputFormatter? best = null;

        foreach (var formatter in this.Formatters)
        {
            if (formatter.CanWrite(value) && (best is null || formatter.Priority > best.Priority))
                best = formatter;
        }

        return best;
    }

    /// <summary>
    /// Picks the formatter for an incoming body, or null when nothing reads this <c>Content-Type</c>
    /// into this type — which the caller turns into a 415.
    /// </summary>
    public IInputFormatter? SelectInput(HttpRequest request, Type targetType)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(targetType);

        var mediaType = BareMediaType(request.ContentType);

        IInputFormatter? best = null;

        foreach (var formatter in this.InputFormatters)
        {
            if (!formatter.CanRead(mediaType, targetType))
                continue;

            if (best is null || formatter.Priority > best.Priority)
                best = formatter;
        }

        return best;
    }

    /// <summary>
    /// Strips parameters and case from a <c>Content-Type</c>: <c>application/json; charset=utf-8</c>
    /// is a statement about a format and an encoding, and only the format picks a formatter.
    /// </summary>
    internal static string BareMediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return string.Empty;

        var semicolon = contentType.IndexOf(';');
        var span = semicolon >= 0 ? contentType.AsSpan(0, semicolon) : contentType.AsSpan();

        return span.Trim().ToString();
    }

    /// <summary>
    /// The formatters used when an app never called <c>AddContentNegotiation</c>. JSON in both
    /// directions, which is the behaviour this server had before formatters existed.
    /// </summary>
    internal static ContentNegotiationOptions Default { get; } = new();
}

/// <summary>
/// A response whose representation is chosen from the request's <c>Accept</c> header.
/// <para>
/// The difference from <c>Results.Ok(value)</c> is who decides the format. That one always writes
/// JSON; this one asks the caller. Worth it when the same endpoint serves a browser, a script and
/// an API client — and not worth it when everything speaks JSON, which is why it is opt-in.
/// </para>
/// </summary>
public sealed class NegotiatedResult(object? value, int statusCode = StatusCodes.Status200OK) : IActionResult
{
    public object? Value { get; } = value;

    public int StatusCode { get; } = statusCode;

    /// <summary>Formatters to use instead of the registered ones, for a server without a container.</summary>
    public ContentNegotiationOptions? Options { get; init; }

    public async ValueTask ExecuteAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = this.Options
            ?? context.RequestServices.GetService<ContentNegotiationOptions>()
            ?? ContentNegotiationOptions.Default;

        var formatter = options.Select(context.Request, this.Value);

        if (formatter is null)
        {
            if (options.ReturnNotAcceptable)
            {
                // 406 with no body: the whole problem is that there is no representation the client
                // said it could read, and inventing one for the error would be the same mistake.
                context.Response.StatusCode = StatusCodes.Status406NotAcceptable;
                context.Response.ContentLength = 0;

                await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
                return;
            }

            formatter = options.Fallback(this.Value)
                ?? throw new InvalidOperationException(
                    $"No formatter can write {this.Value?.GetType().Name ?? "null"}."
                );
        }

        context.Response.StatusCode = this.StatusCode;
        context.Response.ContentType = formatter.Charset is { } charset
            ? $"{formatter.MediaType}; charset={charset}"
            : formatter.MediaType;

        // Announced whether or not it varied this time: a shared cache that stored the JSON copy
        // without it would serve JSON to a client that asked for text.
        context.Response.Headers.Append(HeaderNames.Vary, HeaderNames.Accept);

        await formatter.WriteAsync(context, this.Value, context.RequestAborted).ConfigureAwait(false);
    }
}

/// <summary>Registering content negotiation.</summary>
public static class ContentNegotiationExtensions
{
    /// <summary>
    /// Registers the formatters used by <c>Results.Negotiate</c>.
    /// <code>
    /// builder.Services.AddContentNegotiation(o =>
    ///     o.AddFormatter("text/csv", (ctx, value, ct) => ctx.Response.WriteTextAsync(ToCsv(value), "text/csv", ct))
    /// );
    /// </code>
    /// </summary>
    public static IServiceCollection AddContentNegotiation(
        this IServiceCollection services,
        Action<ContentNegotiationOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddSingleton(_ =>
        {
            var options = new ContentNegotiationOptions();
            configure?.Invoke(options);

            return options;
        });
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Shiny.Net.HttpServer.Negotiation;

namespace Shiny.Net.HttpServer.Endpoints;

/// <summary>
/// A typed body read: the value, or the reason there isn't one.
/// <para>
/// Separate from a <c>bool</c> because "no body", "unreadable body" and "body in a format I do not
/// speak" are three different answers, and only the last one is a 415.
/// </para>
/// </summary>
public readonly record struct BodyReadResult<T>(BodyReadStatus Status, T? Value)
{
    public bool Success => this.Status == BodyReadStatus.Success;
}

/// <summary>
/// The runtime half of tier 3. The generator decides <em>what</em> to bind at compile time and
/// calls into here to actually do it.
/// <para>
/// Everything is generic over <see cref="IParsable{TSelf}"/> or <see cref="Enum"/> rather than
/// switching on <see cref="Type"/>, so each call site the generator emits resolves to a concrete
/// instantiation the compiler can see. No reflection, nothing for the trimmer to guess at — and
/// the same helpers work for a user's own type as long as it implements <c>IParsable</c>.
/// </para>
/// </summary>
public static class EndpointBinder
{
    /// <summary>Where a value was being read from, used to write a useful 400.</summary>
    public enum Source
    {
        Route,
        Query,
        Header,
        Body
    }

    // ---- Scalars ----

    public static bool TryBind<T>(string? raw, [MaybeNullWhen(false)] out T value) where T : IParsable<T>
    {
        if (raw is null)
        {
            value = default;
            return false;
        }
        return T.TryParse(raw, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Binds a nullable value type. A missing value succeeds with null; a present but unparseable
    /// value fails — "absent" and "wrong" are different answers and only one of them is a 400.
    /// </summary>
    public static bool TryBindNullable<T>(string? raw, out T? value) where T : struct, IParsable<T>
    {
        if (string.IsNullOrEmpty(raw))
        {
            value = null;
            return true;
        }

        if (T.TryParse(raw, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        value = null;
        return false;
    }

    public static bool TryBindEnum<T>(string? raw, out T value) where T : struct, Enum
    {
        if (string.IsNullOrEmpty(raw))
        {
            value = default;
            return false;
        }
        return Enum.TryParse(raw, ignoreCase: true, out value);
    }

    public static bool TryBindNullableEnum<T>(string? raw, out T? value) where T : struct, Enum
    {
        if (string.IsNullOrEmpty(raw))
        {
            value = null;
            return true;
        }

        if (Enum.TryParse<T>(raw, ignoreCase: true, out var parsed))
        {
            value = parsed;
            return true;
        }

        value = null;
        return false;
    }

    // ---- Repeated query values ----

    public static bool TryBindArray<T>(StringValues raw, [MaybeNullWhen(false)] out T[] value)
        where T : IParsable<T>
    {
        if (raw.Count == 0)
        {
            value = [];
            return true;
        }

        var result = new T[raw.Count];
        for (var i = 0; i < raw.Count; i++)
        {
            if (raw[i] is not { } item || !T.TryParse(item, CultureInfo.InvariantCulture, out var parsed))
            {
                value = null;
                return false;
            }
            result[i] = parsed;
        }

        value = result;
        return true;
    }

    public static string[] BindStringArray(StringValues raw)
    {
        if (raw.Count == 0)
            return [];

        var result = new string[raw.Count];
        for (var i = 0; i < raw.Count; i++)
            result[i] = raw[i] ?? string.Empty;

        return result;
    }

    // ---- Body ----

    /// <summary>
    /// Deserializes the JSON request body. Returns false (leaving the caller to write a 400) when
    /// the body is absent, not JSON, or deserializes to null.
    /// </summary>
    public static async ValueTask<(bool Success, T? Value)> TryReadJsonBodyAsync<T>(
        HttpContext context,
        JsonTypeInfo<T> typeInfo
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Request.HasBody)
            return (false, default);

        try
        {
            var value = await JsonSerializer
                .DeserializeAsync(context.Request.Body, typeInfo, context.RequestAborted)
                .ConfigureAwait(false);

            return value is null ? (false, default) : (true, value);
        }
        catch (JsonException)
        {
            // Malformed JSON is the client's mistake, not the server's — it becomes a 400 rather
            // than bubbling up into the connection's 500 path.
            return (false, default);
        }
    }

    /// <summary>
    /// Deserializes the JSON request body using metadata from <see cref="JsonTypeInfoRegistry"/>.
    /// <para>
    /// Kept for handlers that want JSON and only JSON regardless of what the caller declared.
    /// Generated endpoints go through <see cref="TryReadBodyAsync{T}"/> instead.
    /// </para>
    /// </summary>
    public static ValueTask<(bool Success, T? Value)> TryReadJsonBodyAsync<T>(HttpContext context)
        => TryReadJsonBodyAsync(context, JsonTypeInfoRegistry.GetRequired<T>());

    /// <summary>
    /// Reads the request body in whichever format the caller declared, using the
    /// <see cref="IInputFormatter"/> registered for its <c>Content-Type</c>.
    /// <para>
    /// This is what generated endpoints call, which is what makes <c>[FromBody]</c> format-agnostic:
    /// an app that registers the XML or MessagePack formatters gets XML and MessagePack request
    /// bodies on every existing endpoint without touching a handler. With nothing registered it is
    /// JSON, exactly as before.
    /// </para>
    /// </summary>
    public static async ValueTask<BodyReadResult<T>> TryReadBodyAsync<T>(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Request.HasBody)
            return new BodyReadResult<T>(BodyReadStatus.NoBody, default);

        var options = context.RequestServices.GetService<ContentNegotiationOptions>()
            ?? ContentNegotiationOptions.Default;

        var formatter = options.SelectInput(context.Request, typeof(T));

        // No formatter for this Content-Type is a 415, not a 400: the body may be perfectly valid
        // and simply in a format nobody here reads, and saying so is the difference between a client
        // fixing its header and a client hunting for a syntax error that is not there.
        if (formatter is null)
            return new BodyReadResult<T>(BodyReadStatus.UnsupportedMediaType, default);

        var result = await formatter
            .ReadAsync(context, typeof(T), context.RequestAborted)
            .ConfigureAwait(false);

        return result.Value is T typed
            ? new BodyReadResult<T>(BodyReadStatus.Success, typed)
            : new BodyReadResult<T>(result.Success ? BodyReadStatus.Malformed : result.Status, default);
    }

    // ---- Failure and completion ----

    /// <summary>
    /// Writes the response for a body that could not be read — a 415 when the format was the
    /// problem, a 400 when the content was.
    /// </summary>
    public static ValueTask BodyReadFailedAsync(
        HttpContext context,
        string parameterName,
        BodyReadStatus status,
        string typeName
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        if (status != BodyReadStatus.UnsupportedMediaType)
            return BindFailedAsync(context, parameterName, Source.Body, typeName);

        var options = context.RequestServices.GetService<ContentNegotiationOptions>()
            ?? ContentNegotiationOptions.Default;

        var supported = string.Join(", ", options.InputFormatters.Select(f => f.MediaType).Distinct());

        context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;

        return context.Response.WriteTextAsync(
            $"The request body content type '{ContentNegotiationOptions.BareMediaType(context.Request.ContentType)}' "
                + $"cannot be read as {typeName}. Supported content types: {supported}.",
            cancellationToken: context.RequestAborted
        );
    }

    /// <summary>Writes the 400 for a parameter that could not be bound.</summary>
    public static ValueTask BindFailedAsync(HttpContext context, string parameterName, Source source, string typeName)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.StatusCode = StatusCodes.Status400BadRequest;

        var where = source switch
        {
            Source.Route => "route value",
            Source.Query => "query parameter",
            Source.Header => "header",
            _ => "request body"
        };

        return context.Response.WriteTextAsync(
            source == Source.Body
                ? $"The request body could not be read as {typeName}."
                : $"The {where} '{parameterName}' is missing or is not a valid {typeName}.",
            cancellationToken: context.RequestAborted
        );
    }

    /// <summary>Executes a handler's result, treating a null result as "nothing more to send".</summary>
    public static ValueTask ExecuteAsync(HttpContext context, IResult? result)
    {
        ArgumentNullException.ThrowIfNull(context);
        return result is null ? CompleteAsync(context) : result.ExecuteAsync(context);
    }

    /// <summary>
    /// Ends a response a handler chose not to write to. Handlers that wrote directly to
    /// <c>ctx.Response</c> have already started, so this is a no-op for them.
    /// </summary>
    public static ValueTask CompleteAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Response.HasStarted)
            return ValueTask.CompletedTask;

        context.Response.ContentLength ??= 0;
        return context.Response.StartAsync(context.RequestAborted);
    }
}

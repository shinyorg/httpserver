using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Primitives;

namespace Shiny.Net.HttpServer.Endpoints;

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
    /// This is the overload generated endpoints use: the app registers its
    /// <c>JsonSerializerContext</c> once and every <c>[FromBody]</c> parameter is covered.
    /// </summary>
    public static ValueTask<(bool Success, T? Value)> TryReadJsonBodyAsync<T>(HttpContext context)
        => TryReadJsonBodyAsync(context, JsonTypeInfoRegistry.GetRequired<T>());

    // ---- Failure and completion ----

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

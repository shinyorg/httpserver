using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Shiny.Net.HttpServer.Sessions;

/// <summary>
/// Per-visitor state that outlives a request.
/// <para>
/// Injected as a scoped service, so a handler or an endpoint class takes an <see cref="ISession"/>
/// in its constructor and never touches <see cref="HttpContext"/> to get at it.
/// </para>
/// <code>
/// public sealed class CartEndpoints(ISession session) : IHttpEndpoint
/// {
///     public void Map(IEndpointRouteBuilder endpoints) =>
///         endpoints.MapPost("/cart/{sku}", async ctx =>
///         {
///             var count = session.GetInt32("items") ?? 0;
///             session.SetInt32("items", count + 1);
///
///             await session.CommitAsync(ctx.RequestAborted);
///         });
/// }
/// </code>
/// </summary>
public interface ISession
{
    /// <summary>
    /// The session's identifier. Stable across requests, and never sent to the client in the clear —
    /// the cookie carries an encrypted form.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// True once the session has been loaded from the store. Reading or writing anything loads it,
    /// so an endpoint that never touches the session costs nothing.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Keys currently set. Loads the session.</summary>
    IReadOnlyCollection<string> Keys { get; }

    ValueTask LoadAsync(CancellationToken cancellationToken = default);

    bool TryGetValue(string key, [NotNullWhen(true)] out byte[]? value);

    void Set(string key, byte[] value);

    void Remove(string key);

    /// <summary>Empties the session without ending it — the id and the cookie stay.</summary>
    void Clear();

    /// <summary>
    /// Writes the session to the store.
    /// <para>
    /// Also done automatically when the response completes. Calling it explicitly matters when the
    /// handler is about to do something that depends on the write having landed — redirecting to a
    /// page that reads it back, for instance.
    /// </para>
    /// </summary>
    ValueTask CommitAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reading and writing the usual types without hand-rolling the byte conversion.</summary>
public static class SessionExtensions
{
    public static string? GetString(this ISession session, string key)
    {
        ArgumentNullException.ThrowIfNull(session);

        return session.TryGetValue(key, out var value) ? Encoding.UTF8.GetString(value) : null;
    }

    public static void SetString(this ISession session, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(value);

        session.Set(key, Encoding.UTF8.GetBytes(value));
    }

    public static int? GetInt32(this ISession session, string key)
    {
        ArgumentNullException.ThrowIfNull(session);

        return session.TryGetValue(key, out var value) && value.Length == 4
            ? BinaryPrimitives.ReadInt32BigEndian(value)
            : null;
    }

    public static void SetInt32(this ISession session, string key, int value)
    {
        ArgumentNullException.ThrowIfNull(session);

        var buffer = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);

        session.Set(key, buffer);
    }

    public static long? GetInt64(this ISession session, string key)
    {
        ArgumentNullException.ThrowIfNull(session);

        return session.TryGetValue(key, out var value) && value.Length == 8
            ? BinaryPrimitives.ReadInt64BigEndian(value)
            : null;
    }

    public static void SetInt64(this ISession session, string key, long value)
    {
        ArgumentNullException.ThrowIfNull(session);

        var buffer = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);

        session.Set(key, buffer);
    }

    public static bool? GetBoolean(this ISession session, string key)
    {
        ArgumentNullException.ThrowIfNull(session);

        return session.TryGetValue(key, out var value) && value.Length == 1 ? value[0] != 0 : null;
    }

    public static void SetBoolean(this ISession session, string key, bool value)
    {
        ArgumentNullException.ThrowIfNull(session);

        session.Set(key, [value ? (byte)1 : (byte)0]);
    }

    public static Guid? GetGuid(this ISession session, string key)
    {
        ArgumentNullException.ThrowIfNull(session);

        return session.TryGetValue(key, out var value) && value.Length == 16 ? new Guid(value) : null;
    }

    public static void SetGuid(this ISession session, string key, Guid value)
    {
        ArgumentNullException.ThrowIfNull(session);

        session.Set(key, value.ToByteArray());
    }

    public static DateTimeOffset? GetDateTimeOffset(this ISession session, string key)
    {
        var ticks = session.GetInt64(key);

        return ticks is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(ticks.Value);
    }

    public static void SetDateTimeOffset(this ISession session, string key, DateTimeOffset value)
        => session.SetInt64(key, value.ToUnixTimeMilliseconds());

    /// <summary>
    /// Reads a value written by <see cref="SetString"/> and parses it with the invariant culture, so
    /// a session written on one machine reads the same on another.
    /// </summary>
    public static double? GetDouble(this ISession session, string key)
        => session.GetString(key) is { } text
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;

    public static void SetDouble(this ISession session, string key, double value)
        => session.SetString(key, value.ToString(CultureInfo.InvariantCulture));
}

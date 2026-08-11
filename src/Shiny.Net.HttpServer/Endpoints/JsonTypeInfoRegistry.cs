using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Shiny.Net.HttpServer;

/// <summary>
/// The bridge between "I just want to return an object" and the AOT guarantee.
/// <para>
/// Serializing a value whose type is only known at runtime normally means reflection, which is
/// exactly what a trimmed or AOT-published app cannot do. Instead an app registers its
/// <see cref="JsonSerializerContext"/> — the source generator emits a module initializer that does
/// it automatically — and lookups become a dictionary hit against compile-time metadata.
/// </para>
/// <para>
/// Metadata is returned from the owning context rather than rebuilt against fresh options, so
/// <c>Results.Ok(widget)</c> and <c>Results.Ok(widget, MyJson.Default.Widget)</c> produce byte-identical
/// output. Two spellings of the same intent must not disagree about property casing.
/// </para>
/// </summary>
public static class JsonTypeInfoRegistry
{
    static readonly object Gate = new();

    // Snapshotted arrays rather than lists: lookups happen on the response path and must not take
    // the lock, and registration happens once at startup.
    static volatile JsonSerializerContext[] contexts = [];
    static volatile IJsonTypeInfoResolver[] resolvers = [];
    static volatile JsonSerializerOptions? resolverOptions;

    /// <summary>
    /// Adds a source-generated context. Safe to call repeatedly and from several assemblies —
    /// for a type covered by more than one context, the first registration wins.
    /// </summary>
    public static void Register(JsonSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        lock (Gate)
        {
            if (Array.IndexOf(contexts, context) >= 0)
                return;

            contexts = [.. contexts, context];
        }
    }

    /// <summary>
    /// Adds a bare resolver, for metadata that does not come from a context. Consulted after every
    /// registered context.
    /// </summary>
    public static void Register(IJsonTypeInfoResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        if (resolver is JsonSerializerContext context)
        {
            Register(context);
            return;
        }

        lock (Gate)
        {
            if (Array.IndexOf(resolvers, resolver) >= 0)
                return;

            resolvers = [.. resolvers, resolver];

            // Rebuilt rather than mutated: a JsonSerializerOptions freezes the moment it is used
            // for real work, so the only way to add a resolver later is a fresh instance.
            resolverOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                TypeInfoResolver = resolvers.Length == 1
                    ? resolvers[0]
                    : JsonTypeInfoResolver.Combine(resolvers)
            };
        }
    }

    /// <summary>True when nothing has been registered yet.</summary>
    public static bool IsEmpty => contexts.Length == 0 && resolvers.Length == 0;

    public static bool TryGet(Type type, [NotNullWhen(true)] out JsonTypeInfo? typeInfo)
    {
        ArgumentNullException.ThrowIfNull(type);

        foreach (var context in contexts)
        {
            if (context.GetTypeInfo(type) is { } fromContext)
            {
                typeInfo = fromContext;
                return true;
            }
        }

        if (resolverOptions is { } options && options.TryGetTypeInfo(type, out var fromResolver))
        {
            typeInfo = fromResolver;
            return true;
        }

        typeInfo = null;
        return false;
    }

    public static bool TryGet<T>([NotNullWhen(true)] out JsonTypeInfo<T>? typeInfo)
    {
        if (TryGet(typeof(T), out var untyped) && untyped is JsonTypeInfo<T> typed)
        {
            typeInfo = typed;
            return true;
        }

        typeInfo = null;
        return false;
    }

    /// <summary>
    /// Looks up type info, throwing an exception that names the missing type and the fix. A bare
    /// <c>NullReferenceException</c> at serialization time would send you looking in the wrong
    /// place entirely.
    /// </summary>
    public static JsonTypeInfo GetRequired(Type type)
        => TryGet(type, out var typeInfo) ? typeInfo : throw Missing(type);

    public static JsonTypeInfo<T> GetRequired<T>()
        => TryGet<T>(out var typeInfo) ? typeInfo : throw Missing(typeof(T));

    static InvalidOperationException Missing(Type type) => new(
        $"No JSON metadata is registered for '{type.FullName}'. " +
        $"Add [JsonSerializable(typeof({type.Name}))] to a JsonSerializerContext in your app; " +
        "contexts are registered with this registry automatically by the endpoint generator, or " +
        "call JsonTypeInfoRegistry.Register(MyContext.Default) yourself."
    );
}

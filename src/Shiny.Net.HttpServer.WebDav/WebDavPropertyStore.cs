using System.Collections.Concurrent;

namespace Shiny.Net.HttpServer.WebDav;

/// <summary>
/// An XML element name: a namespace URI and a local name.
/// <para>
/// The prefix a client happened to use is not part of the identity. <c>&lt;Z:foo xmlns:Z="urn:x"/&gt;</c>
/// and <c>&lt;q:foo xmlns:q="urn:x"/&gt;</c> are the same property, and a store that keyed on the
/// prefix would lose one of them.
/// </para>
/// </summary>
/// <param name="Namespace">The namespace URI. Empty for an unqualified name.</param>
/// <param name="Name">The local name.</param>
public readonly record struct WebDavPropertyName(string Namespace, string Name)
{
    /// <summary>Live properties defined by RFC 4918 live here.</summary>
    public const string DavNamespace = "DAV:";

    /// <summary>True for a property this server computes rather than stores.</summary>
    public bool IsDav => string.Equals(this.Namespace, DavNamespace, StringComparison.Ordinal);

    public override string ToString() => this.Namespace.Length == 0 ? this.Name : $"{{{this.Namespace}}}{this.Name}";
}

/// <summary>A dead property: a name and the XML that was inside it.</summary>
/// <param name="Name">The property's element name.</param>
/// <param name="Xml">
/// The element's inner XML, exactly as it arrived. Kept raw rather than parsed because a dead
/// property is opaque by definition — the server's job is to give back what it was handed.
/// </param>
public sealed record WebDavProperty(WebDavPropertyName Name, string Xml);

/// <summary>
/// Where dead properties are kept.
/// <para>
/// Separate from the files themselves because there is nowhere on a file system to put them. The
/// default keeps them in memory; an implementation that wants them to survive a restart writes them
/// wherever the app already keeps its state.
/// </para>
/// <para>
/// Paths are relative to the mount root, use forward slashes, and never start or end with one. The
/// root collection itself is the empty string.
/// </para>
/// </summary>
public interface IWebDavPropertyStore
{
    /// <summary>Every dead property on a resource. Empty when it has none.</summary>
    ValueTask<IReadOnlyList<WebDavProperty>> GetAsync(string path, CancellationToken cancellationToken);

    /// <summary>Sets one property, replacing any existing value.</summary>
    ValueTask SetAsync(string path, WebDavProperty property, CancellationToken cancellationToken);

    /// <summary>Removes one property. Removing one that is not there is not an error.</summary>
    ValueTask RemoveAsync(string path, WebDavPropertyName name, CancellationToken cancellationToken);

    /// <summary>
    /// Drops everything held for a resource, and for its members when it was a collection.
    /// Called after a <c>DELETE</c>, and for the destination of an overwriting <c>COPY</c>/<c>MOVE</c>.
    /// </summary>
    ValueTask DeleteAsync(string path, bool recursive, CancellationToken cancellationToken);

    /// <summary>
    /// Moves what is held for a resource, and for its members, to a new path. Properties travel
    /// with the resource on a <c>MOVE</c>, and are duplicated on a <c>COPY</c>.
    /// </summary>
    ValueTask CopyAsync(string fromPath, string toPath, bool move, CancellationToken cancellationToken);
}

/// <summary>
/// Dead properties held in memory, for as long as the process lives.
/// <para>
/// The default, and the honest one for an embedded server: the files are on a device whose app is
/// the thing being restarted, and a property store that silently lost data at a different moment
/// than the obvious one would be harder to reason about than one that loses it at a known one.
/// </para>
/// </summary>
public sealed class InMemoryWebDavPropertyStore : IWebDavPropertyStore
{
    readonly ConcurrentDictionary<string, ConcurrentDictionary<WebDavPropertyName, string>> byPath = new(StringComparer.Ordinal);

    public ValueTask<IReadOnlyList<WebDavProperty>> GetAsync(string path, CancellationToken cancellationToken)
    {
        if (!this.byPath.TryGetValue(path, out var properties) || properties.IsEmpty)
            return new ValueTask<IReadOnlyList<WebDavProperty>>(Array.Empty<WebDavProperty>());

        var result = new List<WebDavProperty>(properties.Count);
        foreach (var pair in properties)
            result.Add(new WebDavProperty(pair.Key, pair.Value));

        return new ValueTask<IReadOnlyList<WebDavProperty>>(result);
    }

    public ValueTask SetAsync(string path, WebDavProperty property, CancellationToken cancellationToken)
    {
        this.byPath.GetOrAdd(path, _ => new ConcurrentDictionary<WebDavPropertyName, string>())[property.Name] = property.Xml;

        return default;
    }

    public ValueTask RemoveAsync(string path, WebDavPropertyName name, CancellationToken cancellationToken)
    {
        if (this.byPath.TryGetValue(path, out var properties))
        {
            properties.TryRemove(name, out _);

            if (properties.IsEmpty)
                this.byPath.TryRemove(path, out _);
        }

        return default;
    }

    public ValueTask DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
    {
        this.byPath.TryRemove(path, out _);

        if (recursive)
        {
            foreach (var key in this.byPath.Keys)
            {
                if (IsUnder(key, path))
                    this.byPath.TryRemove(key, out _);
            }
        }

        return default;
    }

    public ValueTask CopyAsync(string fromPath, string toPath, bool move, CancellationToken cancellationToken)
    {
        // Snapshotted first: the loop below writes into the same dictionary it is reading, and a
        // copy into a path underneath the source would otherwise walk its own output.
        var sources = new List<string>();

        if (this.byPath.ContainsKey(fromPath))
            sources.Add(fromPath);

        foreach (var key in this.byPath.Keys)
        {
            if (IsUnder(key, fromPath))
                sources.Add(key);
        }

        foreach (var source in sources)
        {
            if (!this.byPath.TryGetValue(source, out var properties))
                continue;

            var suffix = source.Length == fromPath.Length ? string.Empty : source[fromPath.Length..].TrimStart('/');
            var target = suffix.Length == 0 ? toPath : (toPath.Length == 0 ? suffix : toPath + "/" + suffix);

            var clone = new ConcurrentDictionary<WebDavPropertyName, string>(properties);
            this.byPath[target] = clone;

            if (move)
                this.byPath.TryRemove(source, out _);
        }

        return default;
    }

    static bool IsUnder(string candidate, string ancestor)
        => ancestor.Length == 0
            ? candidate.Length > 0
            : candidate.Length > ancestor.Length
                && candidate.StartsWith(ancestor, StringComparison.Ordinal)
                && candidate[ancestor.Length] == '/';
}

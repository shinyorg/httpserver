using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Shiny.Net.HttpServer.CalDav.Internal;

/// <summary>
/// Sync tokens (RFC 6578) for stores that know nothing about them.
/// <para>
/// A token is a hash of a collection's listing — every object's name and ETag — so it changes when
/// anything in the collection does, whoever changed it: a client of this server, or the app writing
/// to its own store directly. Each token handed out keeps the listing it was made from, and a
/// <c>sync-collection</c> with that token is answered by comparing it with the listing now: what is
/// new or has a different ETag changed, and what is missing was removed.
/// </para>
/// <para>
/// Snapshots are held in memory, a bounded number per collection. A token that has aged out, or one
/// from before a restart, is answered with <c>valid-sync-token</c> — except that a token equal to the
/// current listing's needs no snapshot at all, because nothing has changed since it was made. That
/// makes the common case after a restart, a client checking an unchanged calendar, still free.
/// </para>
/// </summary>
sealed class SyncTracker(int maxPerCollection)
{
    const string TokenPrefix = "http://shinylib.net/ns/sync/";

    readonly ConcurrentDictionary<string, Ring> collections = new(StringComparer.Ordinal);

    /// <summary>The token for a listing, remembering the listing so the token can be synced from later.</summary>
    public string Register(string collectionKey, IReadOnlyList<DavObjectInfo> listing)
    {
        var token = TokenFor(collectionKey, listing);
        var snapshot = new Dictionary<string, string>(listing.Count, StringComparer.Ordinal);

        foreach (var item in listing)
            snapshot[item.Name] = item.ETag;

        this.collections.GetOrAdd(collectionKey, _ => new Ring(maxPerCollection)).Add(token, snapshot);

        return token;
    }

    /// <summary>The listing a token was made from, when it is still remembered.</summary>
    public bool TryGetSnapshot(string collectionKey, string token, out IReadOnlyDictionary<string, string> snapshot)
    {
        snapshot = null!;

        return this.collections.TryGetValue(collectionKey, out var ring) && ring.TryGet(token, out snapshot);
    }

    /// <summary>Drops a deleted collection's history, so a new one by the same name starts clean.</summary>
    public void Forget(string collectionKey) => this.collections.TryRemove(collectionKey, out _);

    /// <summary>The hash a token and a derived <c>getctag</c> share.</summary>
    public static string TokenFor(string collectionKey, IReadOnlyList<DavObjectInfo> listing)
        => TokenPrefix + Hash(collectionKey, listing);

    public static string Hash(string collectionKey, IReadOnlyList<DavObjectInfo> listing)
    {
        var builder = new StringBuilder(collectionKey).Append('\n');

        foreach (var item in listing.OrderBy(i => i.Name, StringComparer.Ordinal))
            builder.Append(item.Name).Append('\t').Append(item.ETag).Append('\n');

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())).AsSpan(0, 16));
    }

    public static bool IsOurs(string token) => token.StartsWith(TokenPrefix, StringComparison.Ordinal);

    /// <summary>The last few snapshots of one collection, oldest evicted first.</summary>
    sealed class Ring(int capacity)
    {
        readonly LinkedList<(string Token, Dictionary<string, string> Snapshot)> entries = new();
        readonly Lock gate = new();

        public void Add(string token, Dictionary<string, string> snapshot)
        {
            lock (this.gate)
            {
                // Re-registering the newest token is the common case — every PROPFIND of an
                // unchanged collection — and should not push older ones out.
                for (var node = this.entries.First; node is not null; node = node.Next)
                {
                    if (node.Value.Token == token)
                    {
                        this.entries.Remove(node);
                        break;
                    }
                }

                this.entries.AddFirst((token, snapshot));

                while (this.entries.Count > capacity)
                    this.entries.RemoveLast();
            }
        }

        public bool TryGet(string token, out IReadOnlyDictionary<string, string> snapshot)
        {
            lock (this.gate)
            {
                foreach (var entry in this.entries)
                {
                    if (entry.Token == token)
                    {
                        snapshot = entry.Snapshot;
                        return true;
                    }
                }
            }

            snapshot = null!;
            return false;
        }
    }
}

/// <summary>The two store interfaces behind one non-generic face, so the handler is written once.</summary>
interface IStoreAdapter
{
    Flavor Flavor { get; }

    DavCollection NewCollection(string id);

    ValueTask<IReadOnlyList<DavCollection>> GetCollectionsAsync(string principal, CancellationToken cancellationToken);

    ValueTask<DavCollection?> GetCollectionAsync(string principal, string collection, CancellationToken cancellationToken);

    ValueTask<DavCollection> CreateCollectionAsync(string principal, DavCollection collection, CancellationToken cancellationToken);

    ValueTask UpdateCollectionAsync(string principal, DavCollection collection, CancellationToken cancellationToken);

    ValueTask DeleteCollectionAsync(string principal, string collection, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<DavObjectInfo>> GetObjectsAsync(string principal, string collection, CancellationToken cancellationToken);

    ValueTask<DavObject?> GetObjectAsync(string principal, string collection, string name, CancellationToken cancellationToken);

    ValueTask<string?> PutObjectAsync(string principal, string collection, DavObjectWrite write, CancellationToken cancellationToken);

    ValueTask DeleteObjectAsync(string principal, string collection, string name, CancellationToken cancellationToken);
}

sealed class StoreAdapter<TCollection>(IDavCollectionStore<TCollection> store, Flavor flavor, Func<string, TCollection> create) : IStoreAdapter
    where TCollection : DavCollection
{
    public Flavor Flavor => flavor;

    public DavCollection NewCollection(string id) => create(id);

    public async ValueTask<IReadOnlyList<DavCollection>> GetCollectionsAsync(string principal, CancellationToken cancellationToken)
        => await store.GetCollectionsAsync(principal, cancellationToken).ConfigureAwait(false);

    public async ValueTask<DavCollection?> GetCollectionAsync(string principal, string collection, CancellationToken cancellationToken)
        => await store.GetCollectionAsync(principal, collection, cancellationToken).ConfigureAwait(false);

    public async ValueTask<DavCollection> CreateCollectionAsync(string principal, DavCollection collection, CancellationToken cancellationToken)
        => await store.CreateCollectionAsync(principal, (TCollection)collection, cancellationToken).ConfigureAwait(false);

    public ValueTask UpdateCollectionAsync(string principal, DavCollection collection, CancellationToken cancellationToken)
        => store.UpdateCollectionAsync(principal, (TCollection)collection, cancellationToken);

    public ValueTask DeleteCollectionAsync(string principal, string collection, CancellationToken cancellationToken)
        => store.DeleteCollectionAsync(principal, collection, cancellationToken);

    public ValueTask<IReadOnlyList<DavObjectInfo>> GetObjectsAsync(string principal, string collection, CancellationToken cancellationToken)
        => store.GetObjectsAsync(principal, collection, cancellationToken);

    public ValueTask<DavObject?> GetObjectAsync(string principal, string collection, string name, CancellationToken cancellationToken)
        => store.GetObjectAsync(principal, collection, name, cancellationToken);

    public ValueTask<string?> PutObjectAsync(string principal, string collection, DavObjectWrite write, CancellationToken cancellationToken)
        => store.PutObjectAsync(principal, collection, write, cancellationToken);

    public ValueTask DeleteObjectAsync(string principal, string collection, string name, CancellationToken cancellationToken)
        => store.DeleteObjectAsync(principal, collection, name, cancellationToken);
}

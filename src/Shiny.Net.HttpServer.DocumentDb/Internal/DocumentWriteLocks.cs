using Shiny.DocumentDb;

namespace Shiny.Net.HttpServer.DocumentDb.Internal;

/// <summary>
/// One async lock per document, for the schema-free lane's writes.
/// <para>
/// A schema-free collection has no version for the store to compare, so a JSON Patch — which can only be
/// applied by reading the document, patching it, and writing the whole thing back — has no compare-and-swap to
/// lean on. Without one, two patches that read the same body each write their own result and the first is lost.
/// Serializing the writes to one document through this server is the guard that is available; writers outside
/// this process remain last-writer-wins, which is all an unversioned row can promise.
/// </para>
/// <para>
/// Entries are reference-counted and removed by the last holder out, so the table only ever holds the
/// documents being written right now rather than every id ever touched.
/// </para>
/// </summary>
static class DocumentWriteLocks
{
    internal readonly record struct Key(IDocumentStore Store, string Collection, string Id);

    internal sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Users;
    }

    static readonly Dictionary<Key, Entry> entries = new();

    /// <summary>Waits for exclusive write access to one document; dispose the result to release it.</summary>
    public static async ValueTask<Releaser> AcquireAsync(
        IDocumentStore store,
        string collection,
        string id,
        CancellationToken cancellationToken
    )
    {
        var key = new Key(store, collection, id);
        Entry? entry;

        lock (entries)
        {
            if (!entries.TryGetValue(key, out entry))
            {
                entry = new Entry();
                entries[key] = entry;
            }

            entry.Users++;
        }

        try
        {
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Never held, so only the reference is given back.
            Leave(key, entry);
            throw;
        }

        return new Releaser(key, entry);
    }

    /// <summary>How many requests hold or are waiting on one document's lock. For tests.</summary>
    internal static int Users(IDocumentStore store, string collection, string id)
    {
        lock (entries)
            return entries.TryGetValue(new Key(store, collection, id), out var entry) ? entry.Users : 0;
    }

    static void Leave(Key key, Entry entry)
    {
        lock (entries)
        {
            entry.Users--;
            if (entry.Users == 0)
                entries.Remove(key);
        }
    }

    public readonly struct Releaser : IDisposable
    {
        readonly Key key;
        readonly Entry entry;

        internal Releaser(Key key, Entry entry)
        {
            this.key = key;
            this.entry = entry;
        }

        public void Dispose()
        {
            this.entry.Gate.Release();
            Leave(this.key, this.entry);
        }
    }
}

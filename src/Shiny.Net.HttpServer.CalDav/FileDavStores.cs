using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Shiny.Net.HttpServer.CalDav;

/// <summary>
/// Calendars kept as <c>.ics</c> files in a directory: one folder per principal, one per calendar,
/// one file per object, each byte for byte as the client sent it.
/// <code>
/// {root}/{principal}/{calendar}/.collection   display name, colour, components…
/// {root}/{principal}/{calendar}/{name}.ics    one event or task, as PUT
/// </code>
/// <para>
/// A principal with no calendars is given <see cref="DefaultCollection"/> the first time its home
/// is touched, because a client that finds an empty home has nothing to sync into — iOS shows the
/// account with no calendars in it and leaves it there.
/// </para>
/// <para>
/// ETags are a hash of the file's content, cached against its size and modification time, so a
/// listing reads only files that changed since the last one and two different texts can never share
/// a tag.
/// </para>
/// </summary>
public sealed class FileCalendarStore : ICalendarStore
{
    readonly DirectoryCollectionStore store;

    /// <param name="rootPath">The directory the calendars are kept in. Created if it does not exist.</param>
    public FileCalendarStore(string rootPath)
        => this.store = new DirectoryCollectionStore(rootPath);

    /// <summary>
    /// What a principal gets on first sight, or null to leave an empty home empty. A calendar
    /// called <c>Calendar</c> holding events and tasks by default.
    /// </summary>
    public CalendarCollection? DefaultCollection { get; set; } = new("default") { DisplayName = "Calendar" };

    public async ValueTask<IReadOnlyList<CalendarCollection>> GetCollectionsAsync(string principal, CancellationToken cancellationToken)
    {
        await this.SeedAsync(principal, cancellationToken).ConfigureAwait(false);

        var ids = this.store.ListCollections(principal);
        var result = new List<CalendarCollection>(ids.Count);

        foreach (var id in ids)
        {
            if (await this.GetCollectionAsync(principal, id, cancellationToken).ConfigureAwait(false) is { } collection)
                result.Add(collection);
        }

        return result;
    }

    public async ValueTask<CalendarCollection?> GetCollectionAsync(string principal, string collection, CancellationToken cancellationToken)
    {
        await this.SeedAsync(principal, cancellationToken).ConfigureAwait(false);

        if (await this.store.ReadMetadataAsync(principal, collection, cancellationToken).ConfigureAwait(false) is not { } meta)
            return null;

        var components = meta.GetProperty("COMPONENTS")?.Value;

        return new CalendarCollection(collection)
        {
            DisplayName = meta.GetProperty("DISPLAYNAME")?.GetText(),
            Description = meta.GetProperty("DESCRIPTION")?.GetText(),
            Color = meta.GetProperty("COLOR")?.GetText(),
            TimeZone = meta.GetProperty("TIMEZONE")?.GetText(),
            SupportedComponents = components is { Length: > 0 }
                ? components.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : ["VEVENT", "VTODO"]
        };
    }

    /// <summary>
    /// Gives a principal with no calendars the default one — on any access, not only a listing, so a
    /// client that already knows the URL (or a PUT straight after setup) finds it there.
    /// </summary>
    async ValueTask SeedAsync(string principal, CancellationToken cancellationToken)
    {
        if (this.DefaultCollection is { } seed && this.store.ListCollections(principal).Count == 0)
            await this.CreateCollectionAsync(principal, seed, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CalendarCollection> CreateCollectionAsync(string principal, CalendarCollection collection, CancellationToken cancellationToken)
    {
        this.store.CreateCollection(principal, collection.Id);
        await this.UpdateCollectionAsync(principal, collection, cancellationToken).ConfigureAwait(false);

        return collection;
    }

    public ValueTask UpdateCollectionAsync(string principal, CalendarCollection collection, CancellationToken cancellationToken)
    {
        var meta = DirectoryCollectionStore.Metadata(collection);

        if (collection.Color is { } color)
            meta.Properties.Add(new ContentProperty("COLOR", ContentProperty.EscapeText(color)));

        if (collection.TimeZone is { } zone)
            meta.Properties.Add(new ContentProperty("TIMEZONE", ContentProperty.EscapeText(zone)));

        meta.Properties.Add(new ContentProperty("COMPONENTS", string.Join(',', collection.SupportedComponents)));

        return this.store.WriteMetadataAsync(principal, collection.Id, meta, cancellationToken);
    }

    public ValueTask DeleteCollectionAsync(string principal, string collection, CancellationToken cancellationToken)
        => this.store.DeleteCollection(principal, collection);

    public ValueTask<IReadOnlyList<DavObjectInfo>> GetObjectsAsync(string principal, string collection, CancellationToken cancellationToken)
        => this.store.ListObjectsAsync(principal, collection, cancellationToken);

    public ValueTask<DavObject?> GetObjectAsync(string principal, string collection, string name, CancellationToken cancellationToken)
        => this.store.ReadObjectAsync(principal, collection, name, cancellationToken);

    public ValueTask<string?> PutObjectAsync(string principal, string collection, DavObjectWrite write, CancellationToken cancellationToken)
        => this.store.WriteObjectAsync(principal, collection, write.Name, write.Data, cancellationToken);

    public ValueTask DeleteObjectAsync(string principal, string collection, string name, CancellationToken cancellationToken)
        => this.store.DeleteObject(principal, collection, name);
}

/// <summary>
/// Address books kept as <c>.vcf</c> files in a directory, laid out and versioned exactly as
/// <see cref="FileCalendarStore"/> keeps calendars.
/// </summary>
public sealed class FileAddressBookStore : IAddressBookStore
{
    readonly DirectoryCollectionStore store;

    /// <param name="rootPath">The directory the address books are kept in. Created if it does not exist.</param>
    public FileAddressBookStore(string rootPath)
        => this.store = new DirectoryCollectionStore(rootPath);

    /// <summary>
    /// What a principal gets on first sight, or null to leave an empty home empty. One address book
    /// called <c>Contacts</c> by default — macOS Contacts only ever syncs the first one it finds.
    /// </summary>
    public AddressBookCollection? DefaultCollection { get; set; } = new("default") { DisplayName = "Contacts" };

    public async ValueTask<IReadOnlyList<AddressBookCollection>> GetCollectionsAsync(string principal, CancellationToken cancellationToken)
    {
        await this.SeedAsync(principal, cancellationToken).ConfigureAwait(false);

        var ids = this.store.ListCollections(principal);
        var result = new List<AddressBookCollection>(ids.Count);

        foreach (var id in ids)
        {
            if (await this.GetCollectionAsync(principal, id, cancellationToken).ConfigureAwait(false) is { } collection)
                result.Add(collection);
        }

        return result;
    }

    public async ValueTask<AddressBookCollection?> GetCollectionAsync(string principal, string collection, CancellationToken cancellationToken)
    {
        await this.SeedAsync(principal, cancellationToken).ConfigureAwait(false);

        if (await this.store.ReadMetadataAsync(principal, collection, cancellationToken).ConfigureAwait(false) is not { } meta)
            return null;

        return new AddressBookCollection(collection)
        {
            DisplayName = meta.GetProperty("DISPLAYNAME")?.GetText(),
            Description = meta.GetProperty("DESCRIPTION")?.GetText()
        };
    }

    /// <summary>
    /// Gives a principal with no address books the default one — on any access, not only a listing, so a
    /// client that already knows the URL (or a PUT straight after setup) finds it there.
    /// </summary>
    async ValueTask SeedAsync(string principal, CancellationToken cancellationToken)
    {
        if (this.DefaultCollection is { } seed && this.store.ListCollections(principal).Count == 0)
            await this.CreateCollectionAsync(principal, seed, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<AddressBookCollection> CreateCollectionAsync(string principal, AddressBookCollection collection, CancellationToken cancellationToken)
    {
        this.store.CreateCollection(principal, collection.Id);
        await this.UpdateCollectionAsync(principal, collection, cancellationToken).ConfigureAwait(false);

        return collection;
    }

    public ValueTask UpdateCollectionAsync(string principal, AddressBookCollection collection, CancellationToken cancellationToken)
        => this.store.WriteMetadataAsync(principal, collection.Id, DirectoryCollectionStore.Metadata(collection), cancellationToken);

    public ValueTask DeleteCollectionAsync(string principal, string collection, CancellationToken cancellationToken)
        => this.store.DeleteCollection(principal, collection);

    public ValueTask<IReadOnlyList<DavObjectInfo>> GetObjectsAsync(string principal, string collection, CancellationToken cancellationToken)
        => this.store.ListObjectsAsync(principal, collection, cancellationToken);

    public ValueTask<DavObject?> GetObjectAsync(string principal, string collection, string name, CancellationToken cancellationToken)
        => this.store.ReadObjectAsync(principal, collection, name, cancellationToken);

    public ValueTask<string?> PutObjectAsync(string principal, string collection, DavObjectWrite write, CancellationToken cancellationToken)
        => this.store.WriteObjectAsync(principal, collection, write.Name, write.Data, cancellationToken);

    public ValueTask DeleteObjectAsync(string principal, string collection, string name, CancellationToken cancellationToken)
        => this.store.DeleteObject(principal, collection, name);
}

/// <summary>The directory layout both file stores share.</summary>
sealed class DirectoryCollectionStore
{
    const string MetadataFile = ".collection";

    readonly string root;

    // Keyed by full path; valid while the file's size and write time are unchanged.
    readonly ConcurrentDictionary<string, (long Length, long Ticks, string ETag)> etags = new(StringComparer.Ordinal);

    public DirectoryCollectionStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        this.root = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(this.root);
    }

    public static ContentComponent Metadata(DavCollection collection)
    {
        // The collection's own settings, written in the content-line format the objects beside it
        // already use, so reading them back is the parser this package already has.
        var meta = new ContentComponent("X-SHINY-COLLECTION");

        if (collection.DisplayName is { } name)
            meta.Properties.Add(new ContentProperty("DISPLAYNAME", ContentProperty.EscapeText(name)));

        if (collection.Description is { } description)
            meta.Properties.Add(new ContentProperty("DESCRIPTION", ContentProperty.EscapeText(description)));

        return meta;
    }

    public IReadOnlyList<string> ListCollections(string principal)
    {
        var home = this.HomePath(principal);

        if (!Directory.Exists(home))
            return [];

        return Directory
            .EnumerateDirectories(home)
            .Where(d => File.Exists(Path.Combine(d, MetadataFile)))
            .Select(d => Path.GetFileName(d)!)
            .Select(Uri.UnescapeDataString)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    public void CreateCollection(string principal, string collection)
        => Directory.CreateDirectory(this.CollectionPath(principal, collection));

    public ValueTask DeleteCollection(string principal, string collection)
    {
        var path = this.CollectionPath(principal, collection);

        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);

        return default;
    }

    public async ValueTask<ContentComponent?> ReadMetadataAsync(string principal, string collection, CancellationToken cancellationToken)
    {
        var path = Path.Combine(this.CollectionPath(principal, collection), MetadataFile);

        if (!File.Exists(path))
            return null;

        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

        return ContentComponent.TryParse(text, out var meta, out _) ? meta : new ContentComponent("X-SHINY-COLLECTION");
    }

    public async ValueTask WriteMetadataAsync(string principal, string collection, ContentComponent meta, CancellationToken cancellationToken)
    {
        var directory = this.CollectionPath(principal, collection);
        Directory.CreateDirectory(directory);

        await WriteAtomicallyAsync(Path.Combine(directory, MetadataFile), meta.ToString(), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<DavObjectInfo>> ListObjectsAsync(string principal, string collection, CancellationToken cancellationToken)
    {
        var directory = this.CollectionPath(principal, collection);

        if (!Directory.Exists(directory))
            return [];

        var result = new List<DavObjectInfo>();

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var fileName = Path.GetFileName(file);

            // The metadata file and half-written temporaries both start with a dot.
            if (fileName.StartsWith('.'))
                continue;

            var info = new FileInfo(file);

            if (!info.Exists)
                continue;

            var etag = await this.ETagAsync(info, cancellationToken).ConfigureAwait(false);

            result.Add(new DavObjectInfo(Uri.UnescapeDataString(fileName), etag, info.LastWriteTimeUtc)
            {
                Length = info.Length
            });
        }

        return result;
    }

    public async ValueTask<DavObject?> ReadObjectAsync(string principal, string collection, string name, CancellationToken cancellationToken)
    {
        var info = new FileInfo(this.ObjectPath(principal, collection, name));

        if (!info.Exists)
            return null;

        var bytes = await File.ReadAllBytesAsync(info.FullName, cancellationToken).ConfigureAwait(false);
        var etag = this.Remember(info, bytes);

        return new DavObject(name, etag, Encoding.UTF8.GetString(bytes), info.LastWriteTimeUtc);
    }

    public async ValueTask<string?> WriteObjectAsync(string principal, string collection, string name, string data, CancellationToken cancellationToken)
    {
        var path = this.ObjectPath(principal, collection, name);
        var bytes = Encoding.UTF8.GetBytes(data);

        await WriteAtomicallyAsync(path, bytes, cancellationToken).ConfigureAwait(false);

        return this.Remember(new FileInfo(path), bytes);
    }

    public ValueTask DeleteObject(string principal, string collection, string name)
    {
        var path = this.ObjectPath(principal, collection, name);

        File.Delete(path);
        this.etags.TryRemove(path, out _);

        return default;
    }

    async ValueTask<string> ETagAsync(FileInfo info, CancellationToken cancellationToken)
    {
        if (this.etags.TryGetValue(info.FullName, out var cached) &&
            cached.Length == info.Length &&
            cached.Ticks == info.LastWriteTimeUtc.Ticks)
            return cached.ETag;

        var bytes = await File.ReadAllBytesAsync(info.FullName, cancellationToken).ConfigureAwait(false);

        return this.Remember(info, bytes);
    }

    string Remember(FileInfo info, byte[] content)
    {
        info.Refresh();

        var etag = "\"" + Convert.ToHexStringLower(SHA256.HashData(content).AsSpan(0, 12)) + "\"";
        this.etags[info.FullName] = (info.Length, info.LastWriteTimeUtc.Ticks, etag);

        return etag;
    }

    static ValueTask WriteAtomicallyAsync(string path, string text, CancellationToken cancellationToken)
        => WriteAtomicallyAsync(path, Encoding.UTF8.GetBytes(text), cancellationToken);

    /// <summary>
    /// Written beside the target and moved over it, so a reader — or a crash — never sees half an
    /// object where a whole one was.
    /// </summary>
    static async ValueTask WriteAtomicallyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    string HomePath(string principal) => Path.Combine(this.root, Segment(principal));

    string CollectionPath(string principal, string collection) => Path.Combine(this.HomePath(principal), Segment(collection));

    string ObjectPath(string principal, string collection, string name) => Path.Combine(this.CollectionPath(principal, collection), Segment(name));

    /// <summary>
    /// A name as a file-system segment. Percent-encoded, so a principal like <c>ada@example.com</c>
    /// or a name with a <c>:</c> in it is a legal file name on every platform — and a leading dot is
    /// encoded too, so nothing a client names can collide with the metadata file.
    /// </summary>
    static string Segment(string value)
    {
        var escaped = Uri.EscapeDataString(value);

        return escaped.StartsWith('.') ? "%2E" + escaped[1..] : escaped;
    }
}

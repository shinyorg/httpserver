using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shiny.Net.HttpServer.FileSync.Client.Internal;

/// <summary>
/// What the client last agreed with the server about each path - the "base" every change is judged
/// against. A file whose size and modified time still match its entry has not changed locally; one
/// whose revision matches the server's has not changed remotely.
/// </summary>
sealed class SyncState
{
    /// <summary>The change-feed cursor: every remote change up to here has been applied.</summary>
    public long Cursor { get; set; }

    public Dictionary<string, SyncStateEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static SyncState Load(string path)
    {
        if (!File.Exists(path))
            return new SyncState();

        try
        {
            var loaded = JsonSerializer.Deserialize(File.ReadAllBytes(path), StateJson.Default.SyncState) ?? new SyncState();

            // The comparer is not part of the JSON.
            loaded.Entries = new Dictionary<string, SyncStateEntry>(loaded.Entries, StringComparer.OrdinalIgnoreCase);
            return loaded;
        }
        catch (JsonException)
        {
            // A state file that cannot be read means starting again from cursor 0: everything is
            // compared afresh, and files whose content matches the server are adopted rather than
            // re-transferred, so this costs time but no data.
            return new SyncState();
        }
    }

    public void Save(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(this, StateJson.Default.SyncState));
        File.Move(temp, path, overwrite: true);
    }
}

sealed class SyncStateEntry
{
    public required string Path { get; set; }

    public long Revision { get; set; }

    public long Size { get; set; }

    public string? Hash { get; set; }

    /// <summary>The local file's modified time (UTC ticks) when it was last synced.</summary>
    public long LocalModifiedTicks { get; set; }

    /// <summary>The chunks, with offsets implied by order - also the index of chunks this device already holds.</summary>
    public List<WireChunk> Chunks { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SyncState))]
sealed partial class StateJson : JsonSerializerContext;

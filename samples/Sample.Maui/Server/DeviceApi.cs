using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Shiny.Net.HttpServer;

namespace Sample.Maui.Server;

/// <summary>What the phone reports about itself.</summary>
public sealed record DeviceSummary(
    string Name,
    string Model,
    string Manufacturer,
    string Platform,
    string Version,
    string Idiom,
    bool IsPhysicalDevice,
    double BatteryLevel,
    string PowerState,
    DateTimeOffset ServerTimeUtc
);

/// <summary>One note, for the write half of the demo.</summary>
public sealed record Note(int Id, string Text, DateTimeOffset CreatedUtc);

public sealed record NewNote(string Text);

/// <summary>
/// Serialization metadata for everything crossing the wire.
/// <para>
/// Declared by the app rather than the library, because a source generator cannot see another
/// generator's output — so the app owns its <c>JsonSerializerContext</c> and registers it once.
/// This is also what keeps JSON reflection-free, which matters on iOS where there is no JIT.
/// </para>
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeviceSummary))]
[JsonSerializable(typeof(Note))]
[JsonSerializable(typeof(Note[]))]
[JsonSerializable(typeof(NewNote))]
public partial class ApiJsonContext : JsonSerializerContext;

/// <summary>Notes kept in memory, so the sample needs no storage to demonstrate a write.</summary>
public sealed class NoteStore
{
    readonly ConcurrentDictionary<int, Note> notes = new();
    int nextId;

    public NoteStore()
    {
        this.Add("Open this URL on another device — it is the phone answering.");
        this.Add("Add a note here and it appears in the app.");
    }

    public Note[] All() => [.. this.notes.Values.OrderBy(n => n.Id)];

    public Note Add(string text)
    {
        var note = new Note(Interlocked.Increment(ref this.nextId), text, DateTimeOffset.UtcNow);
        this.notes[note.Id] = note;

        this.Changed?.Invoke(this, note);

        return note;
    }

    public bool Remove(int id) => this.notes.TryRemove(id, out _);

    /// <summary>Raised when a note arrives, so the app can show traffic as it happens.</summary>
    public event EventHandler<Note>? Changed;
}

/// <summary>The endpoints this app serves.</summary>
public static class DeviceApi
{
    public static void Map(HttpServer server, NoteStore notes)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(notes);

        // The simplest possible endpoint: something to curl when you are checking the link works.
        server.MapGet("/ping", ctx => ctx.Response.WriteTextAsync("pong"));

        // Passing the JsonTypeInfo explicitly is the direct path — the compiler sees the metadata
        // and nothing is discovered at runtime.
        server.MapGet("/api/device", _ => Results.Ok(Describe(), ApiJsonContext.Default.DeviceSummary));

        server.MapGet("/api/notes", _ => Results.Ok(notes.All(), ApiJsonContext.Default.NoteArray));

        server.MapPost("/api/notes", async ctx =>
        {
            var submitted = await ctx.Request.ReadJsonAsync(ApiJsonContext.Default.NewNote, ctx.RequestAborted);

            if (submitted is not { Text.Length: > 0 })
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["text"] = ["A note needs some text."]
                });

            var note = notes.Add(submitted.Text);

            return Results.Created($"/api/notes/{note.Id}", note, ApiJsonContext.Default.Note);
        });

        server.MapDelete("/api/notes/{id:int}", ctx =>
        {
            var id = int.Parse(ctx.Request.RouteValues["id"]!.ToString()!, System.Globalization.CultureInfo.InvariantCulture);

            return notes.Remove(id)
                ? ctx.Response.WriteTextAsync("deleted")
                : ValueTask.CompletedTask;
        });
    }

    /// <summary>Internal rather than private: the MCP tools report the same device.</summary>
    internal static DeviceSummary Describe()
    {
        // Battery is not readable on every platform or in every emulator, and an unhandled
        // exception here would take down a request that is otherwise fine.
        var (level, power) = TryReadBattery();

        return new DeviceSummary(
            DeviceInfo.Current.Name,
            DeviceInfo.Current.Model,
            DeviceInfo.Current.Manufacturer,
            DeviceInfo.Current.Platform.ToString(),
            DeviceInfo.Current.VersionString,
            DeviceInfo.Current.Idiom.ToString(),
            DeviceInfo.Current.DeviceType == DeviceType.Physical,
            level,
            power,
            DateTimeOffset.UtcNow
        );
    }

    static (double Level, string Power) TryReadBattery()
    {
        try
        {
            return (Battery.Default.ChargeLevel, Battery.Default.State.ToString());
        }
        catch (Exception ex) when (ex is FeatureNotSupportedException or PermissionException or NotImplementedException)
        {
            return (-1, "Unknown");
        }
    }
}

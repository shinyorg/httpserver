using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer.Switchboard;

namespace Sample.Switchboard;

public sealed record ChatMessage(string Room, string User, string Text, DateTimeOffset At);

public sealed record RoomInfo(string Name, int Members);

/// <summary>What the chat switchboard calls on its clients.</summary>
public interface IChatClient
{
    Task MessageReceived(ChatMessage message);

    Task UserJoined(string room, string user);

    Task UserLeft(string room, string user);

    Task Notice(string text);

    /// <summary>A client result: the server waits for the answer.</summary>
    Task<bool> ConfirmDelete(string room);
}

[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(RoomInfo))]
[JsonSerializable(typeof(IReadOnlyList<RoomInfo>))]
public partial class ChatJson : JsonSerializerContext;

/// <summary>In-memory rooms and history. A real app would put a database here.</summary>
public sealed class RoomStore
{
    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> members = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, ConcurrentQueue<ChatMessage>> history = new(StringComparer.OrdinalIgnoreCase);

    public void Join(string room, string user) => this.members.GetOrAdd(room, _ => new())[user] = 0;

    public void Leave(string room, string user)
    {
        if (this.members.TryGetValue(room, out var users))
            users.TryRemove(user, out _);
    }

    public void Add(ChatMessage message)
    {
        var queue = this.history.GetOrAdd(message.Room, _ => new());
        queue.Enqueue(message);

        while (queue.Count > 200)
            queue.TryDequeue(out _);
    }

    public IReadOnlyList<RoomInfo> List() => [.. this.members.Select(m => new RoomInfo(m.Key, m.Value.Count)).OrderBy(r => r.Name)];

    public IEnumerable<ChatMessage> Read(string room, int take)
        => this.history.TryGetValue(room, out var queue) ? queue.TakeLast(take) : [];

    public void Delete(string room)
    {
        this.members.TryRemove(room, out _);
        this.history.TryRemove(room, out _);
    }
}

/// <summary>
/// The chat switchboard. [ExportContract] publishes its contract — IChatBoard, IChatClient, the two
/// records and a JSON context — into the client sample's Contracts/ folder on every build.
/// </summary>
[ExportContract(Namespace = "Sample.Switchboard.Contracts")]
public class ChatBoard(RoomStore rooms, ILogger<ChatBoard> logger) : Switchboard<IChatClient>
{
    string Me => this.Context.UserIdentifier ?? "anonymous";

    // Per-line state. Calls from one line run one at a time, so no locking.
    HashSet<string> JoinedRooms
    {
        get
        {
            if (!this.Context.Items.TryGetValue("rooms", out var value))
                this.Context.Items["rooms"] = value = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return (HashSet<string>)value!;
        }
    }

    public override Task OnConnectedAsync()
    {
        logger.LogInformation("{User} connected on line {LineId}", this.Me, this.Context.LineId);
        return this.Clients.Caller.Notice($"Welcome, {this.Me}");
    }

    public override Task OnReconnectedAsync()
    {
        logger.LogInformation("{User} resumed line {LineId}", this.Me, this.Context.LineId);
        return Task.CompletedTask;
    }

    public override async Task OnDisconnectedAsync(DisconnectContext disconnect)
    {
        foreach (var room in this.JoinedRooms)
        {
            rooms.Leave(room, this.Me);
            await this.Clients.OthersInGroup(room).UserLeft(room, this.Me);
        }

        logger.LogInformation("{User} left ({Reason}{Message})", this.Me, disconnect.Reason, disconnect.Message is { } m ? ": " + m : "");
    }

    public async Task JoinRoom(string room)
    {
        if (!this.JoinedRooms.Add(room))
            return;

        await this.Groups.AddToGroupAsync(this.Context.LineId, room);
        rooms.Join(room, this.Me);
        await this.Clients.OthersInGroup(room).UserJoined(room, this.Me);
    }

    public async Task LeaveRoom(string room)
    {
        if (!this.JoinedRooms.Remove(room))
            return;

        await this.Groups.RemoveFromGroupAsync(this.Context.LineId, room);
        rooms.Leave(room, this.Me);
        await this.Clients.Group(room).UserLeft(room, this.Me);
    }

    public async Task SendMessage(string room, string text)
    {
        if (!this.JoinedRooms.Contains(room))
            throw new SwitchboardException($"Join '{room}' before posting to it");

        var message = new ChatMessage(room, this.Me, text, DateTimeOffset.UtcNow);
        rooms.Add(message);
        await this.Clients.Group(room).MessageReceived(message);
    }

    public IReadOnlyList<RoomInfo> ListRooms() => rooms.List();

    public async IAsyncEnumerable<ChatMessage> History(string room, int take = 20, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var message in rooms.Read(room, take))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return message;
            await Task.Yield();
        }
    }

    public async Task DeleteRoom(string room)
    {
        // Asks the caller and waits for the answer.
        if (!await this.Clients.Caller.ConfirmDelete(room))
            throw new SwitchboardException("Delete cancelled");

        await this.Clients.Group(room).Notice($"'{room}' has been deleted");
        await this.Groups.RemoveGroupAsync(room);
        rooms.Delete(room);
    }
}

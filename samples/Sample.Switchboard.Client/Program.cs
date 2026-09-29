using Sample.Switchboard.Contracts;
using Shiny.Net.HttpServer.Switchboard.Client;

// A console chat client for Sample.Switchboard. It knows the server only through
// Contracts/ChatBoard.Contract.g.cs — the file the server's build exports.
//
//   dotnet run --project samples/Sample.Switchboard.Client -- alice
//
// Commands: /join <room>, /rooms, /delete <room>, /quit. Anything else is a message.
// With --smoke it connects, round-trips a message, streams history and exits: 0 on success — a quick
// check that a Native AOT build of both samples still works end to end.

var smoke = args.Contains("--smoke");
var user = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? Environment.UserName;
var url = Environment.GetEnvironmentVariable("SWITCHBOARD_URL") ?? "http://127.0.0.1:5080/chat";
var currentRoom = "general";
TaskCompletionSource<bool>? pendingConfirm = null;

var line = new SwitchboardLineBuilder()
    .WithUrl(url, o => o.Headers["X-User"] = user)
    .WithJson(ChatBoardContractJsonContext.Default)
    .WithAutomaticReconnect()
    .Build();

await using var chat = new ChatBoardClient(line);

// ---- line state ----

line.Reconnecting += (_, e) => Log($"… line dropped ({e.Exception?.Message ?? "closed by the server"}), attempt {e.Attempt}");

line.Reconnected += async (_, e) =>
{
    if (e.NewLine)
    {
        Log("… reconnected on a new line; rejoining");
        await chat.JoinRoom(currentRoom);
    }
    else
    {
        Log(e.MissedMessages ? "… reconnected, some messages were lost" : "… reconnected, nothing missed");
    }
};

line.Closed += (_, e) => Log(e.Reason switch
{
    LineCloseReason.ClientClosed => "Signed off.",
    LineCloseReason.HungUp => $"The server hung up: {e.Message}",
    LineCloseReason.RetriesExhausted => "Could not reconnect. Giving up.",
    _ => $"Line closed: {e.Reason} {e.Message ?? e.Exception?.Message}"
});

// ---- server → client ----

var received = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

chat.MessageReceived += m =>
{
    Log($"[{m.Room}] {m.User}: {m.Text}");
    received.TrySetResult(m);
    return Task.CompletedTask;
};
chat.UserJoined += (room, who) => { Log($"* {who} joined {room}"); return Task.CompletedTask; };
chat.UserLeft += (room, who) => { Log($"* {who} left {room}"); return Task.CompletedTask; };
chat.Notice += text => { Log($"! {text}"); return Task.CompletedTask; };

// The server waits for this answer. It runs off the message queue, so other messages keep arriving
// while it waits for the next line of input.
chat.ConfirmDelete = room =>
{
    Log($"Delete '{room}' and all its history? [y/N]");
    pendingConfirm = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    return pendingConfirm.Task;
};

// ---- go ----

await line.StartAsync();
Log($"Connected as {user} on line {line.LineId}");
await chat.JoinRoom(currentRoom);

if (smoke)
{
    await chat.SendMessage(currentRoom, "smoke test");
    var echoed = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var rooms = await chat.ListRooms();

    var history = 0;
    await foreach (var _ in chat.History(currentRoom, 5))
        history++;

    await line.StopAsync();

    var ok = echoed.Text == "smoke test" && rooms.Any(r => r.Name == currentRoom) && history > 0;
    Log(ok ? "SMOKE OK" : "SMOKE FAILED");
    return ok ? 0 : 1;
}

foreach (var room in await chat.ListRooms())
    Log($"  #{room.Name} ({room.Members} online)");

await PrintHistory();

while (Console.ReadLine() is { } input && line.State != LineState.Disconnected)
{
    if (Interlocked.Exchange(ref pendingConfirm, null) is { } confirm)
    {
        confirm.TrySetResult(input is "y" or "Y");
        continue;
    }

    try
    {
        switch (input.Split(' ', 2))
        {
            case ["/quit"]:
                await line.StopAsync();
                return 0;

            case ["/rooms"]:
                foreach (var room in await chat.ListRooms())
                    Log($"  #{room.Name} ({room.Members} online)");
                break;

            case ["/join", var room]:
                await chat.LeaveRoom(currentRoom);
                currentRoom = room;
                await chat.JoinRoom(room);
                await PrintHistory();
                break;

            case ["/delete", var room]:
                // Not awaited: the server asks us to confirm while the call is still running.
                _ = DeleteRoom(room);
                break;

            default:
                await chat.SendMessage(currentRoom, input);
                break;
        }
    }
    catch (SwitchboardException ex)
    {
        Log($"x {ex.Message}");
    }
}

return 0;

async Task DeleteRoom(string room)
{
    try
    {
        await chat.DeleteRoom(room);
        Log($"Deleted '{room}'");
    }
    catch (SwitchboardException ex)
    {
        Log($"x {ex.Message}");
    }
}

async Task PrintHistory()
{
    await foreach (var m in chat.History(currentRoom, 20))
        Log($"  [{m.At:t}] {m.User}: {m.Text}");
}

static void Log(string text) => Console.WriteLine(text);

[SwitchboardClient<IChatBoard, IChatClient>]
public partial class ChatBoardClient;

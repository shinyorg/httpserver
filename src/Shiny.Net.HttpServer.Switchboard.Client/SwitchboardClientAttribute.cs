namespace Shiny.Net.HttpServer.Switchboard.Client;

/// <summary>
/// Generates a strongly typed client for a switchboard from its contract interfaces.
/// <code>
/// [SwitchboardClient&lt;IChatBoard, IChatClient&gt;]
/// public partial class ChatBoardClient;
///
/// await using var chat = new ChatBoardClient(line);
/// chat.MessageReceived += m => { … };          // IChatClient methods become events…
/// chat.ConfirmDelete = room => AskAsync(room);  // …or, when the server waits for an answer, a handler property
/// await chat.Line.StartAsync();
/// await chat.SendMessage("general", "hi");      // IChatBoard methods are calls
/// </code>
/// The class implements <typeparamref name="TBoard"/>. Every <typeparamref name="TClient"/> method
/// becomes an event (or, for one returning <c>Task&lt;T&gt;</c>, a single handler property), and
/// <c>Register(TClient)</c> binds an implementation of the whole interface instead.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SwitchboardClientAttribute<TBoard, TClient> : Attribute
    where TBoard : class
    where TClient : class;

/// <summary>Generates a strongly typed client for a switchboard that does not call its clients back.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SwitchboardClientAttribute<TBoard> : Attribute
    where TBoard : class;

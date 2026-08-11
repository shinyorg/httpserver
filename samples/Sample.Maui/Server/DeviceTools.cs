using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Sample.Maui.Server;

/// <summary>
/// The same device and the same notes as <see cref="DeviceApi"/>, exposed to an AI client instead of
/// a browser.
/// <para>
/// This is the part ASP.NET Core cannot do: the MCP SDK's own HTTP transport is an ASP.NET Core
/// package, and ASP.NET Core does not run in a MAUI app. Hosting it here means the thing on the other
/// end of the conversation is the phone itself — its battery, its storage, its state — rather than a
/// service that has been told about the phone.
/// </para>
/// <para>
/// Tools are ordinary methods, and <see cref="NoteStore"/> arrives by injection because the MCP
/// server is built from the app's own container.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class DeviceTools
{
    [McpServerTool(Name = "get_device")]
    [Description("Reads what this device reports about itself: name, model, OS version and battery.")]
    public static DeviceSummary GetDevice() => DeviceApi.Describe();

    [McpServerTool(Name = "list_notes")]
    [Description("Lists the notes currently held by the app.")]
    public static Note[] ListNotes(NoteStore notes) => notes.All();

    [McpServerTool(Name = "add_note")]
    [Description("Adds a note. It appears on the app's screen straight away.")]
    public static Note AddNote(NoteStore notes, [Description("The text of the note.")] string text)
        => notes.Add(text);

    [McpServerTool(Name = "remove_note")]
    [Description("Removes a note by id. Returns false when there was no note with that id.")]
    public static bool RemoveNote(NoteStore notes, [Description("The id of the note to remove.")] int id)
        => notes.Remove(id);
}

using System.Net;
using Shiny.Net.HttpServer.Switchboard.Internal;

namespace Shiny.Net.HttpServer.Switchboard;

/// <summary>Whether a line currently has an open stream.</summary>
public enum LineStatus
{
    /// <summary>The client's event stream is open.</summary>
    Attached,

    /// <summary>
    /// The stream dropped and the line is being held for the resume window. Messages sent to it are
    /// kept and replayed if it comes back.
    /// </summary>
    Detached
}

/// <summary>A snapshot of one line.</summary>
public sealed record LineInfo(
    string Id,
    string? User,
    IReadOnlyCollection<string> Groups,
    DateTimeOffset ConnectedAt,
    LineStatus Status,
    int Resumes,
    string Protocol,
    IPAddress? RemoteAddress
);

/// <summary>Every line on one switchboard: who is connected, and hanging up on them.</summary>
public interface ILineManager
{
    int Count { get; }

    /// <summary>A snapshot. Safe to enumerate while lines come and go.</summary>
    IReadOnlyList<LineInfo> Lines { get; }

    LineInfo? Find(string lineId);

    /// <summary>
    /// Hangs up on one line. The client is sent <paramref name="reason"/>, its token stops working at
    /// once, and — unless <paramref name="allowReconnect"/> — it does not come back.
    /// Returns false when there was no such line.
    /// </summary>
    Task<bool> HangUpAsync(string lineId, string? reason = null, bool allowReconnect = false);

    /// <summary>Hangs up on every line a user has open. Returns how many.</summary>
    Task<int> HangUpUserAsync(string userId, string? reason = null, bool allowReconnect = false);

    /// <summary>Hangs up on every line in a group. Returns how many.</summary>
    Task<int> HangUpGroupAsync(string group, string? reason = null, bool allowReconnect = false);

    /// <summary>
    /// Hangs up on everyone. Reconnecting is allowed by default because the usual reason is a server
    /// stopping for a while — a MAUI app going to the background should not tell its clients to give
    /// up forever.
    /// </summary>
    Task HangUpAllAsync(string? reason = null, bool allowReconnect = true);
}

/// <summary>
/// Group membership. Groups exist by being joined and disappear when their last line leaves; a
/// line keeps its groups across a resume.
/// </summary>
public interface IGroupManager
{
    Task AddToGroupAsync(string lineId, string group, CancellationToken cancellationToken = default);

    Task RemoveFromGroupAsync(string lineId, string group, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes every line out of <paramref name="group"/>. Nobody is hung up on — that is
    /// <see cref="ILineManager.HangUpGroupAsync"/>. Returns how many lines left.
    /// </summary>
    Task<int> RemoveGroupAsync(string group, CancellationToken cancellationToken = default);

    /// <summary>The ids of the lines currently in <paramref name="group"/>.</summary>
    IReadOnlyCollection<string> GetLines(string group);
}

sealed class LineManager(BoardRuntime runtime) : ILineManager
{
    public int Count => runtime.Count;

    public IReadOnlyList<LineInfo> Lines => [.. runtime.AllLines().Select(l => l.ToInfo())];

    public LineInfo? Find(string lineId) => runtime.FindLine(lineId)?.ToInfo();

    public Task<bool> HangUpAsync(string lineId, string? reason = null, bool allowReconnect = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(lineId);

        var closed = runtime.FindLine(lineId) is { } line
            && runtime.Close(line, DisconnectReason.HungUp, reason, allowReconnect);

        return Task.FromResult(closed);
    }

    public Task<int> HangUpUserAsync(string userId, string? reason = null, bool allowReconnect = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        return Task.FromResult(this.HangUpWhere(l => string.Equals(l.UserId, userId, StringComparison.Ordinal), DisconnectReason.HungUp, reason, allowReconnect));
    }

    public Task<int> HangUpGroupAsync(string group, string? reason = null, bool allowReconnect = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(group);
        return Task.FromResult(this.HangUpWhere(l => l.InGroup(group), DisconnectReason.HungUp, reason, allowReconnect));
    }

    public Task HangUpAllAsync(string? reason = null, bool allowReconnect = true)
    {
        this.HangUpWhere(static _ => true, DisconnectReason.HungUp, reason, allowReconnect);
        return Task.CompletedTask;
    }

    int HangUpWhere(Func<Line, bool> filter, DisconnectReason disconnectReason, string? reason, bool allowReconnect)
    {
        var count = 0;

        foreach (var line in runtime.AllLines())
        {
            if (filter(line) && runtime.Close(line, disconnectReason, reason, allowReconnect))
                count++;
        }

        return count;
    }
}

sealed class GroupManager(BoardRuntime runtime) : IGroupManager
{
    public Task AddToGroupAsync(string lineId, string group, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(lineId);
        ArgumentException.ThrowIfNullOrEmpty(group);

        // An unknown line is a no-op rather than an error: the line may have closed between the
        // caller reading its id and getting here, and that race is nobody's bug.
        runtime.FindLine(lineId)?.JoinGroup(group);
        return Task.CompletedTask;
    }

    public Task RemoveFromGroupAsync(string lineId, string group, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(lineId);
        ArgumentException.ThrowIfNullOrEmpty(group);

        runtime.FindLine(lineId)?.LeaveGroup(group);
        return Task.CompletedTask;
    }

    public Task<int> RemoveGroupAsync(string group, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(group);

        var removed = 0;
        foreach (var line in runtime.AllLines())
        {
            if (line.LeaveGroup(group))
                removed++;
        }

        return Task.FromResult(removed);
    }

    public IReadOnlyCollection<string> GetLines(string group)
    {
        ArgumentException.ThrowIfNullOrEmpty(group);
        return [.. runtime.AllLines().Where(l => l.InGroup(group)).Select(l => l.Id)];
    }
}

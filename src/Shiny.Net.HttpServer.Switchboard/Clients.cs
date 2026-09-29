using Shiny.Net.HttpServer.Switchboard.Internal;

namespace Shiny.Net.HttpServer.Switchboard;

/// <summary>
/// One or more lines, called as a unit. Sending returns as soon as the call is queued for every
/// line it addresses — it does not wait for delivery, and a line that has gone away is skipped
/// rather than failing the send for everyone else.
/// </summary>
public interface IClientProxy
{
    /// <summary>
    /// Calls <paramref name="method"/> on the client with <paramref name="args"/>. Each argument is
    /// serialized with the JSON metadata registered for its runtime type.
    /// </summary>
    Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default);
}

/// <summary>The spelling most calls use.</summary>
public static class ClientProxyExtensions
{
    /// <summary>
    /// Calls <paramref name="method"/> on the client.
    /// <code>
    /// await Clients.Group("kitchen").SendAsync("DoorUnlocked", doorId, DateTimeOffset.UtcNow);
    /// </code>
    /// </summary>
    public static Task SendAsync(this IClientProxy proxy, string method, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        return proxy.SendCoreAsync(method, args);
    }
}

/// <summary>
/// Exactly one line — which, unlike a group, can be asked a question and waited on.
/// </summary>
public interface ISingleLineProxy : IClientProxy
{
    /// <summary>
    /// Calls <paramref name="method"/> on the client and waits for the value its handler returns.
    /// <code>
    /// if (!await Clients.Caller.InvokeAsync&lt;bool&gt;("ConfirmDelete", [room], ct))
    ///     throw new SwitchboardException("Cancelled");
    /// </code>
    /// Throws <see cref="SwitchboardException"/> with the client's message when its handler failed or
    /// it has none, <see cref="TimeoutException"/> after <see cref="SwitchboardOptions.ClientResultTimeout"/>,
    /// and <see cref="LineClosedException"/> when the line closes for good first. A drop inside the
    /// resume window is not a failure: the question is replayed when the line comes back.
    /// </summary>
    Task<T> InvokeAsync<T>(string method, object?[] args, CancellationToken cancellationToken = default);
}

/// <summary>The line a call was waiting on closed for good, or never existed.</summary>
public sealed class LineClosedException(string message) : Exception(message);

/// <summary>
/// Addresses lines on a switchboard. <see cref="Caller"/>, <see cref="Others"/> and
/// <see cref="OthersInGroup"/> exist only inside a switchboard call, where there is a caller to
/// be relative to.
/// </summary>
public interface ISwitchboardClients<out T>
{
    /// <summary>Every line on the switchboard.</summary>
    T All { get; }

    T AllExcept(params IReadOnlyList<string> lineIds);

    /// <summary>The line that made the current call.</summary>
    T Caller { get; }

    /// <summary>Every line except the caller's.</summary>
    T Others { get; }

    T Line(string lineId);

    T Lines(params IReadOnlyList<string> lineIds);

    T Group(string group);

    /// <summary>Every line in any of <paramref name="groups"/>, once each.</summary>
    T Groups(params IReadOnlyList<string> groups);

    T GroupExcept(string group, params IReadOnlyList<string> excludedLineIds);

    /// <summary>Every line in <paramref name="group"/> except the caller's.</summary>
    T OthersInGroup(string group);

    /// <summary>Every line belonging to a user — their phone and their tablet, both.</summary>
    T User(string userId);

    T Users(params IReadOnlyList<string> userIds);
}

/// <summary>
/// The untyped clients of a switchboard. <see cref="Caller"/> and <see cref="Line"/> are single lines,
/// so they can also be asked for a result.
/// </summary>
public interface ISwitchboardCallerClients : ISwitchboardClients<IClientProxy>
{
    new ISingleLineProxy Caller { get; }

    new ISingleLineProxy Line(string lineId);
}

sealed class SwitchboardClients(BoardRuntime runtime, string? callerLineId) : ISwitchboardCallerClients
{
    IClientProxy ISwitchboardClients<IClientProxy>.Caller => this.Caller;

    IClientProxy ISwitchboardClients<IClientProxy>.Line(string lineId) => this.Line(lineId);

    public IClientProxy All => new LineSetProxy(runtime, static _ => true);

    public IClientProxy AllExcept(params IReadOnlyList<string> lineIds)
    {
        var excluded = ToSet(lineIds);
        return new LineSetProxy(runtime, line => !excluded.Contains(line.Id));
    }

    public ISingleLineProxy Caller => new SingleLineProxy(runtime, this.RequireCaller(nameof(this.Caller)));

    public IClientProxy Others
    {
        get
        {
            var caller = this.RequireCaller(nameof(this.Others));
            return new LineSetProxy(runtime, line => line.Id != caller);
        }
    }

    public ISingleLineProxy Line(string lineId)
    {
        ArgumentException.ThrowIfNullOrEmpty(lineId);
        return new SingleLineProxy(runtime, lineId);
    }

    public IClientProxy Lines(params IReadOnlyList<string> lineIds)
    {
        var included = ToSet(lineIds);
        return new LineSetProxy(runtime, line => included.Contains(line.Id));
    }

    public IClientProxy Group(string group)
    {
        ArgumentException.ThrowIfNullOrEmpty(group);
        return new LineSetProxy(runtime, line => line.InGroup(group));
    }

    public IClientProxy Groups(params IReadOnlyList<string> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        return new LineSetProxy(runtime, line =>
        {
            foreach (var group in groups)
            {
                if (line.InGroup(group))
                    return true;
            }

            return false;
        });
    }

    public IClientProxy GroupExcept(string group, params IReadOnlyList<string> excludedLineIds)
    {
        ArgumentException.ThrowIfNullOrEmpty(group);

        var excluded = ToSet(excludedLineIds);
        return new LineSetProxy(runtime, line => line.InGroup(group) && !excluded.Contains(line.Id));
    }

    public IClientProxy OthersInGroup(string group)
    {
        ArgumentException.ThrowIfNullOrEmpty(group);

        var caller = this.RequireCaller(nameof(this.OthersInGroup));
        return new LineSetProxy(runtime, line => line.Id != caller && line.InGroup(group));
    }

    public IClientProxy User(string userId)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        return new LineSetProxy(runtime, line => string.Equals(line.UserId, userId, StringComparison.Ordinal));
    }

    public IClientProxy Users(params IReadOnlyList<string> userIds)
    {
        var included = ToSet(userIds);
        return new LineSetProxy(runtime, line => line.UserId is { } user && included.Contains(user));
    }

    string RequireCaller(string member)
        => callerLineId ?? throw new InvalidOperationException(
            $"Clients.{member} is relative to the line making the current call, and there is none here. " +
            "It is only available inside a switchboard method or lifecycle callback."
        );

    static HashSet<string> ToSet(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new HashSet<string>(values, StringComparer.Ordinal);
    }
}

/// <summary>
/// Serializes the call once and queues the same payload on every matching line. Per-recipient
/// serialization is the classic broadcast cost that grows with the audience; this does not.
/// </summary>
sealed class LineSetProxy(BoardRuntime runtime, Func<Line, bool> filter) : IClientProxy
{
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = InvocationPayload.Write(method, args);
            runtime.Broadcast(SwitchboardProtocol.InvokeEvent, payload, filter);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }
}

sealed class SingleLineProxy(BoardRuntime runtime, string lineId) : ISingleLineProxy
{
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    {
        try
        {
            if (runtime.FindLine(lineId) is { } line)
                runtime.Send(line, SwitchboardProtocol.InvokeEvent, InvocationPayload.Write(method, args));

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    public Task<T> InvokeAsync<T>(string method, object?[] args, CancellationToken cancellationToken = default)
        => runtime.InvokeClientAsync<T>(lineId, method, args, cancellationToken);
}

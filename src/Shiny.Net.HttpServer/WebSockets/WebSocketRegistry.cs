using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.Net.HttpServer.WebSockets;

/// <summary>One tracked socket, plus who is on the other end of it.</summary>
public sealed class WebSocketConnection
{
    internal WebSocketConnection(string id, WebSocket socket, string? user)
    {
        this.Id = id;
        this.Socket = socket;
        this.User = user;
    }

    /// <summary>Stable for the life of the socket. What <c>SendToAsync</c> addresses.</summary>
    public string Id { get; }

    public WebSocket Socket { get; }

    /// <summary>
    /// The authenticated name, when the upgrade request carried one. Null for an anonymous socket.
    /// </summary>
    public string? User { get; }

    /// <summary>The groups this socket has joined.</summary>
    public IReadOnlySet<string> Groups => this.groups;

    readonly HashSet<string> groups = new(StringComparer.Ordinal);

    internal object Gate { get; } = new();

    internal bool JoinGroup(string group)
    {
        lock (this.Gate)
            return this.groups.Add(group);
    }

    internal bool LeaveGroup(string group)
    {
        lock (this.Gate)
            return this.groups.Remove(group);
    }

    internal bool InGroup(string group)
    {
        lock (this.Gate)
            return this.groups.Contains(group);
    }
}

/// <summary>
/// Every socket the server currently holds, addressable one at a time, by group, or all at once.
/// <para>
/// The thing a raw <c>AcceptWebSocketAsync</c> cannot do: a handler owns exactly one socket and has
/// no way to reach the others, so "tell every connected device the door just unlocked" means the
/// app builds its own list, its own locking, and its own dead-socket cleanup. This is that list.
/// </para>
/// <code>
/// app.MapGet("/ws", async ctx =>
/// {
///     await using var connection = await ctx.AcceptTrackedWebSocketAsync(registry);
///     connection.JoinGroup("kitchen");
///
///     while (await connection.Socket.ReceiveAsync(ctx.RequestAborted) is { } message)
///         await registry.SendToGroupAsync("kitchen", message.Text);
/// });
/// </code>
/// </summary>
public interface IWebSocketRegistry
{
    /// <summary>How many sockets are currently tracked.</summary>
    int Count { get; }

    /// <summary>A snapshot of the tracked sockets. Safe to enumerate while others connect and leave.</summary>
    IReadOnlyList<WebSocketConnection> Connections { get; }

    /// <summary>Starts tracking a socket. Returns the handle used to address it.</summary>
    WebSocketConnection Add(WebSocket socket, string? user = null, string? id = null);

    /// <summary>Stops tracking a socket. Does not close it.</summary>
    bool Remove(string id);

    WebSocketConnection? Find(string id);

    /// <summary>Sends a text message to one socket. False when the socket is gone or the send failed.</summary>
    ValueTask<bool> SendToAsync(string id, string message, CancellationToken cancellationToken = default);

    /// <summary>Sends to every socket in a group. Returns how many were reached.</summary>
    ValueTask<int> SendToGroupAsync(string group, string message, CancellationToken cancellationToken = default);

    /// <summary>Sends to every socket belonging to a user — their phone and their tablet, both.</summary>
    ValueTask<int> SendToUserAsync(string user, string message, CancellationToken cancellationToken = default);

    /// <summary>Sends to every tracked socket, optionally filtered. Returns how many were reached.</summary>
    ValueTask<int> BroadcastAsync(
        string message,
        Func<WebSocketConnection, bool>? filter = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>Sends binary data to every tracked socket, optionally filtered.</summary>
    ValueTask<int> BroadcastAsync(
        ReadOnlyMemory<byte> message,
        Func<WebSocketConnection, bool>? filter = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>Closes every tracked socket — a server shutting down, a user signing out everywhere.</summary>
    ValueTask CloseAllAsync(
        WebSocketCloseStatus status = WebSocketCloseStatus.EndpointUnavailable,
        string? description = null,
        CancellationToken cancellationToken = default
    );
}

/// <summary>The in-process registry. One per server is the normal arrangement.</summary>
public sealed class WebSocketRegistry : IWebSocketRegistry
{
    readonly ConcurrentDictionary<string, WebSocketConnection> connections = new(StringComparer.Ordinal);
    long nextId;

    public int Count => this.connections.Count;

    public IReadOnlyList<WebSocketConnection> Connections => [.. this.connections.Values];

    public WebSocketConnection Add(WebSocket socket, string? user = null, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(socket);

        id ??= Interlocked.Increment(ref this.nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);

        var connection = new WebSocketConnection(id, socket, user);
        this.connections[id] = connection;

        return connection;
    }

    public bool Remove(string id) => this.connections.TryRemove(id, out _);

    public WebSocketConnection? Find(string id) => this.connections.GetValueOrDefault(id);

    public async ValueTask<bool> SendToAsync(string id, string message, CancellationToken cancellationToken = default)
    {
        if (this.Find(id) is not { } connection)
            return false;

        return await this.TrySendAsync(connection, message, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<int> SendToGroupAsync(string group, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        return this.BroadcastAsync(message, x => x.InGroup(group), cancellationToken);
    }

    public ValueTask<int> SendToUserAsync(string user, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        return this.BroadcastAsync(message, x => string.Equals(x.User, user, StringComparison.Ordinal), cancellationToken);
    }

    public async ValueTask<int> BroadcastAsync(
        string message,
        Func<WebSocketConnection, bool>? filter = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(message);

        var sent = 0;

        // Sends run concurrently: one slow or half-dead peer must not hold up the broadcast to
        // everyone behind it, which is exactly what a sequential loop does.
        var pending = new List<Task<bool>>(this.connections.Count);

        foreach (var connection in this.connections.Values)
        {
            if (filter is null || filter(connection))
                pending.Add(this.TrySendAsync(connection, message, cancellationToken).AsTask());
        }

        foreach (var result in await Task.WhenAll(pending).ConfigureAwait(false))
        {
            if (result)
                sent++;
        }

        return sent;
    }

    public async ValueTask<int> BroadcastAsync(
        ReadOnlyMemory<byte> message,
        Func<WebSocketConnection, bool>? filter = null,
        CancellationToken cancellationToken = default
    )
    {
        var sent = 0;
        var pending = new List<Task<bool>>(this.connections.Count);

        foreach (var connection in this.connections.Values)
        {
            if (filter is null || filter(connection))
                pending.Add(this.TrySendBytesAsync(connection, message, cancellationToken).AsTask());
        }

        foreach (var result in await Task.WhenAll(pending).ConfigureAwait(false))
        {
            if (result)
                sent++;
        }

        return sent;
    }

    public async ValueTask CloseAllAsync(
        WebSocketCloseStatus status = WebSocketCloseStatus.EndpointUnavailable,
        string? description = null,
        CancellationToken cancellationToken = default
    )
    {
        foreach (var connection in this.connections.Values)
        {
            try
            {
                await connection.Socket.CloseAsync(status, description, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (WebSocket.IsDisconnect(ex))
            {
                // Already gone. Nothing to close.
            }

            this.Remove(connection.Id);
        }
    }

    /// <summary>
    /// Sends, and drops the socket when it cannot be reached.
    /// <para>
    /// A broadcast must never throw because of one dead peer. The alternative — surfacing the
    /// failure — means every caller writes the same try/catch, and the one that forgets takes the
    /// whole notification down for everyone else.
    /// </para>
    /// </summary>
    async ValueTask<bool> TrySendAsync(WebSocketConnection connection, string message, CancellationToken cancellationToken)
    {
        try
        {
            await connection.Socket.SendAsync(message, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (WebSocket.IsDisconnect(ex))
        {
            this.Remove(connection.Id);
            return false;
        }
    }

    async ValueTask<bool> TrySendBytesAsync(WebSocketConnection connection, ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        try
        {
            await connection.Socket.SendAsync(message, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (WebSocket.IsDisconnect(ex))
        {
            this.Remove(connection.Id);
            return false;
        }
    }
}

/// <summary>A tracked socket that untracks itself when the handler returns.</summary>
public sealed class TrackedWebSocket(IWebSocketRegistry registry, WebSocketConnection connection) : IAsyncDisposable
{
    public WebSocketConnection Connection { get; } = connection;

    public WebSocket Socket => this.Connection.Socket;

    /// <summary>Adds this socket to a group. Groups are created by being joined.</summary>
    public TrackedWebSocket JoinGroup(string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);

        this.Connection.JoinGroup(group);
        return this;
    }

    public TrackedWebSocket LeaveGroup(string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);

        this.Connection.LeaveGroup(group);
        return this;
    }

    /// <summary>Reads the next message. Null when the socket closed.</summary>
    public ValueTask<WebSocketMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
        => this.Socket.ReceiveAsync(cancellationToken);

    public ValueTask SendAsync(string message, CancellationToken cancellationToken = default)
        => this.Socket.SendAsync(message, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        registry.Remove(this.Connection.Id);
        await this.Socket.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Registering and using the socket registry.</summary>
public static class WebSocketRegistryExtensions
{
    /// <summary>Registers a singleton <see cref="IWebSocketRegistry"/>.</summary>
    public static ShinyHttpServerBuilder AddWebSocketRegistry(this ShinyHttpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<IWebSocketRegistry, WebSocketRegistry>();
        return builder;
    }

    /// <summary>
    /// Accepts the upgrade and tracks the socket, using the authenticated name when there is one.
    /// Disposing the result untracks it and closes the socket.
    /// </summary>
    public static async ValueTask<TrackedWebSocket> AcceptTrackedWebSocketAsync(
        this HttpContext context,
        IWebSocketRegistry registry,
        WebSocketAcceptOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(registry);

        var socket = await context.AcceptWebSocketAsync(options, cancellationToken).ConfigureAwait(false);
        var connection = registry.Add(socket, context.User.Identity?.Name);

        return new TrackedWebSocket(registry, connection);
    }

    /// <summary>
    /// Accepts the upgrade and tracks the socket, resolving the registry from the request scope.
    /// </summary>
    public static ValueTask<TrackedWebSocket> AcceptTrackedWebSocketAsync(
        this HttpContext context,
        WebSocketAcceptOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        var registry = context.GetService<IWebSocketRegistry>()
            ?? throw new InvalidOperationException(
                "No IWebSocketRegistry is registered. Add it with builder.AddWebSocketRegistry(), " +
                "or pass one to AcceptTrackedWebSocketAsync."
            );

        return context.AcceptTrackedWebSocketAsync(registry, options, cancellationToken);
    }
}

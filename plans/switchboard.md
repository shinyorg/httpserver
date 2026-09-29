# Shiny.Net.HttpServer.Switchboard — SignalR-shaped real-time calls over Server-Sent Events and `IAsyncEnumerable`

**Status:** all six phases built and tested — see §8, including where the build departed from the plan.
**Target version:** 1.5.0 (`version.json` → `1.5.0-beta.{height}`; release notes go under a
`## 1.5 TBD` heading in the documentation repo — create it if it is not there yet).
**Scope:** four new projects in this repo, a sample, tests, docs, skill and readme updates.
**v1 = phases 1–6 below**, client results included. Deferred past v1: client → server streaming
(§8, "After v1"). Not planned: a JavaScript/TypeScript client.

**Decisions taken** (were open questions):
- The package is `Shiny.Net.HttpServer.Switchboard`, and the vocabulary follows it — nothing is
  called "hub". A switchboard fits what this does in both directions: callers dial in on **lines**,
  the board rings one line, a group or everyone, an **operator** works it from outside, a dropped
  line is held open for a while, and the server can **hang up**. The name also says "not SignalR
  wire-compatible" on its own. The high-traffic names (`Clients`, `Groups`, `All`, `Caller`,
  `Others`, `OnConnectedAsync`, `StartAsync`/`StopAsync`) stay plain on purpose.

  | SignalR | Switchboard |
  | --- | --- |
  | `Hub` / `Hub<TClient>` | `Switchboard` / `Switchboard<TClient>` (`class ChatBoard : Switchboard<IChatClient>`) |
  | `IHubContext<THub, TClient>` | `IOperator<TBoard, TClient>` |
  | `MapHub<T>` / `AddSignalR` | `MapSwitchboard<T>` / `AddSwitchboard` |
  | connection / `ConnectionId` | line / `LineId` (`Clients.Line(id)`, `Clients.Lines(...)`) |
  | `HubCallerContext` | `CallerContext` |
  | `Context.Abort()` | `Context.HangUp()`, `ILineManager.HangUpAsync` / `HangUpUserAsync` / `HangUpGroupAsync` / `HangUpAllAsync` |
  | `HubException` | `SwitchboardException` |
  | `HubConnection` / `HubConnectionBuilder` | `SwitchboardLine` / `SwitchboardLineBuilder` |
  | `[HubMethodName]` / `[NonHubMethod]` | `[LineMethod]` / `[NotALineMethod]` |
  | `ISingleClientProxy` | `ISingleLineProxy` |
- Client results (server awaits a value from one client) are in v1 (§4.7).
- No JS/TS client. The .NET client is the only client.
- Resume (reconnect with the same line id, groups and missed messages) is on by default.
- The server generator can emit the contract interfaces as a shareable source file for clients
  that cannot reference a shared contracts assembly (§6.1).

| Project | Ships as | What it is |
| --- | --- | --- |
| `Shiny.Net.HttpServer.Switchboard` | package | Server: `Switchboard`, `Switchboard<TClient>`, `IOperator<TBoard>`, `ILineManager`, `MapSwitchboard<TBoard>()` |
| `Shiny.Net.HttpServer.Switchboard.SourceGenerators` | packed into `.Switchboard` (analyzers/dotnet/cs) | Switchboard dispatchers + `Switchboard<TClient>` client proxies + optional contract export — no reflection |
| `Shiny.Net.HttpServer.Switchboard.Client` | package | .NET client: `SwitchboardLine`, `On<…>`, `InvokeAsync`, `StreamAsync`, reconnect, state events. **No dependency on the server package** — runs in MAUI, Blazor WASM, console |
| `Shiny.Net.HttpServer.Switchboard.Client.SourceGenerators` | packed into `.Switchboard.Client` | Strongly typed client proxies from a shared contract interface |

The packaging mirrors `Mediator` + `Mediator.SourceGenerators`: the generator and the attributes it
reads are one feature, so they ship together.

---

## 1. Why not just SignalR, and why not WebSockets

- **SignalR is ASP.NET Core.** The server half cannot load on MAUI, which is the reason this
  library exists. Its client proxies (`Hub<T>` server-side, `IHubContext<THub,T>`) are built with
  `Reflection.Emit` — not AOT-clean.
- **WebSockets already exist here** (`WebSockets/WebSocketRegistry.cs`) and are the right tool for
  high-rate bidirectional traffic. SSE + POST is the right tool for everything else:
  - survives proxies, corporate middleboxes and every `ITunnelProvider` that can stream a chunked
    response — no upgrade handshake to break;
  - multiplexes over HTTP/2 and HTTP/3 (one TCP/QUIC connection for the event stream *and* every
    invocation), where WebSockets over HTTP/2 (RFC 8441) is rarely supported by intermediaries;
  - every upstream call is an ordinary HTTP request, so the whole middleware pipeline applies to it
    unchanged: auth, `[Authorize]`, rate limiting, IP filtering, CORS, telemetry, access logs;
  - `IAsyncEnumerable<T>` maps naturally onto an event stream in both directions.

**Explicit non-goal:** wire compatibility with SignalR clients. This is SignalR-*shaped* (same
concepts, similar names so the mental model transfers), not a SignalR implementation. The docs page
must say so in the first paragraph.

## 2. Building blocks that already exist

| Existing piece | Reused for |
| --- | --- |
| `Sse/ServerSentEventStream` (`Sse/ServerSentEventStream.cs`) | The downstream writer. Note it is **not** safe for concurrent sends (shared `StringBuilder`), so each connection gets a single writer loop fed by a channel (§5.3). |
| `WebSockets/WebSocketRegistry` | The shape of the connection registry (id / user / groups, concurrent broadcast that drops dead peers without throwing). Switchboard gets its own registry — the WS one is keyed to `WebSocket` — but copy its semantics. |
| `Mcp/McpHttpSessionManager` | The pattern for sessions that outlive a request: random 128-bit ids, sweeper started lazily, `Environment.TickCount64` idle clock, `MaxSessions` guard rail. |
| `Endpoints/JsonTypeInfoRegistry` | AOT-safe (de)serialization of switchboard arguments and results. |
| `SourceGenerators/EndpointGenerator` | Parameter-binding conventions (`[FromServices]`, `CancellationToken`) and `[Authorize]` metadata handling the switchboard generator should match. |
| `Timeouts/` | The connect endpoint must be mapped with `DisableRequestTimeout()`. |
| `Testing/TestHttpServer` | In-memory end-to-end tests of server + client. |

## 3. Wire protocol (v1)

One switchboard is mounted at a base path, e.g. `/chat`. Everything is JSON unless noted.

### 3.1 Endpoints

| Request | Purpose |
| --- | --- |
| `GET  {base}/connect` | Opens the downstream event stream. New connection, or resume with `?token=` + `Last-Event-ID`. |
| `POST {base}/invoke` | Client → server invocation. Response is the result (`application/json`), or an event stream when the switchboard method returns `IAsyncEnumerable<T>`. |
| `POST {base}/completion` | Client's answer to a server → client invocation that expects a result (§4.7). |
| `DELETE {base}/connect` | Graceful client-initiated disconnect. |

Every `POST`/`DELETE` carries `X-Switchboard-Line: <lineToken>`.

### 3.2 Identity: line id vs line token

- **Line id** — public. What `Clients.Line(id)` addresses, what apps log and hand to other
  users. Sequential-ish is fine.
- **Line token** — secret, 128 random bits (same reasoning as the MCP session id: it is a
  bearer token). Required on every upstream request and on resume. Knowing someone's line id
  must not let you speak as them.
- When the connect request was authenticated, every upstream request must authenticate as the
  **same principal name** or it is rejected with 403. The token proves "this line", the auth
  pipeline proves "this user"; both are checked.

### 3.3 Downstream events (`text/event-stream`)

Every event that carries data has an `id:` — a per-connection monotonically increasing sequence
number. That is what makes resume work.

| `event:` | `data:` | Meaning |
| --- | --- | --- |
| `connected` | `{ "protocol": 1, "lineId", "lineToken", "heartbeatMs", "resumeWindowMs" }` | First event on a new connection. |
| `resumed` | `{ "lineId", "missed": false }` | First event on a resumed connection; replay follows. `missed: true` when the replay buffer could not cover the gap (§5.4). |
| `invoke` | `{ "target": "ReceiveMessage", "args": [ … ], "invocationId"?: "…" }` | Server → client call. `invocationId` present only when the server awaits a result. |
| `close` | `{ "reason": "…", "allowReconnect": false }` | Server is ending the connection. Client must not auto-reconnect when `allowReconnect` is false. |
| *(comment)* | `: ping` | Heartbeat every `heartbeatMs`. Keeps idle proxies from closing the stream and lets the client detect a dead one. |

### 3.4 Upstream invocation

```
POST /chat/invoke
X-Switchboard-Line: 9f1c…
Content-Type: application/json

{ "target": "SendMessage", "args": ["allan", "hello"], "seq": 42 }
```

- Returns `200 { "result": … }`, `204` for void, or `4xx/5xx` with a problem-details body
  (`{ "error": "…" }`). Switchboard exceptions are **not** surfaced verbatim unless
  `SwitchboardOptions.EnableDetailedErrors` — same default as SignalR, same reason.
- `SwitchboardException` messages are always surfaced (it is the "I meant the client to see this" type).
- A switchboard method returning `IAsyncEnumerable<T>` answers with `text/event-stream`: one `item` event
  per element, then `complete` (or `error`). Client disposal / cancellation aborts the request,
  which cancels the enumerator via `RequestAborted` — cancellation is free.
- `seq` exists for ordering (§5.5).

## 4. Server API

### 4.1 Registration

```csharp
builder.AddSwitchboard(o =>
{
    o.HeartbeatInterval = TimeSpan.FromSeconds(15);
    o.ResumeWindow = TimeSpan.FromSeconds(30);   // how long a dropped connection is held for resume
    o.ReplayBufferSize = 256;                    // events kept per connection for resume
    o.MaxBufferedMessagesPerLine = 1024;   // outbound backpressure bound (§5.3)
    o.EnableDetailedErrors = false;
});

app.MapSwitchboard<ChatBoard>("/chat")
   .RequireAuthorization();                      // returns a route-group builder like other Map* calls
```

`MapSwitchboard<TBoard>` resolves a generated `SwitchboardDispatcher<TBoard>` from a registry filled by a module
initializer (the same trick `JsonTypeInfoRegistry` uses). A switchboard type with no generated dispatcher is
a build-time diagnostic, not a runtime surprise: the generator reports `SWBxxx` for any
`MapSwitchboard<T>` whose `T` is not a discovered switchboard.

### 4.2 Switchboard

```csharp
[Authorize]                                        // existing attribute; class or method level
public class ChatBoard(IMessageStore store) : Switchboard<IChatClient>
{
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.LineId, "lobby");
        await Clients.Others.UserJoined(Context.UserIdentifier);
    }

    public override Task OnReconnectedAsync()      // resumed inside the resume window — groups intact
        => Clients.Caller.Welcome("welcome back");

    public override Task OnDisconnectedAsync(DisconnectContext disconnect)
        => Clients.Others.UserLeft(Context.UserIdentifier, disconnect.Reason.ToString());

    public async Task SendMessage(string text)
    {
        await store.SaveAsync(Context.UserIdentifier, text);
        await Clients.Group("lobby").ReceiveMessage(Context.UserIdentifier, text);
    }

    public Task<int> OnlineCount() => Task.FromResult(/* … */ 0);

    // Server → client stream: the POST response becomes an event stream.
    public async IAsyncEnumerable<Tick> Ticks(int count, [EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
        {
            yield return new Tick(i, DateTimeOffset.UtcNow);
            await Task.Delay(1000, ct);
        }
    }

    [LineMethod("kick")]
    [Authorize(Roles = "admin")]
    public Task Kick(string lineId) => Lines.HangUpAsync(lineId, "Removed by an admin");
}
```

- **Switchboard lifetime:** one instance per invocation / lifecycle callback, constructed in a DI scope for
  that request (the same model as `[Route]` endpoint classes and as SignalR). State that must persist
  goes in `Context.Items` or a service.
- **`Switchboard`** (untyped) exposes `Clients` as `ISwitchboardClients<IClientProxy>` with
  `SendAsync(string method, params object?[] args)` — the params array is serialized per-argument via
  `JsonTypeInfoRegistry`, so an unregistered type fails fast with a message naming the missing
  `JsonSerializerContext`, never a silent reflection fallback.
- **`Switchboard<TClient>`** exposes `ISwitchboardClients<TClient>`; the generator emits the `TClient`
  implementation over `IClientProxy` (§6.1).
- **Method discovery:** public instance methods declared on the switchboard. `[LineMethod]` renames,
  `[NotALineMethod]` excludes. Supported returns: `void`, `Task`, `ValueTask`, `Task<T>`,
  `ValueTask<T>`, `T`, `IAsyncEnumerable<T>`. Parameters: JSON args (positional), plus
  `CancellationToken` (= `RequestAborted` of the POST), `[FromServices]`, `CallerContext`.
  Anything else is a generator diagnostic.
- **Parameters of type `IAsyncEnumerable<T>`** (client → server streaming) are deferred past v1 (§8);
  until then the generator reports them as unsupported.

### 4.3 Context, Clients, Groups, Lines

```csharp
public abstract class Switchboard
{
    public CallerContext Context { get; }
    public ISwitchboardClients<IClientProxy> Clients { get; }   // Switchboard<T>: ISwitchboardClients<T>
    public IGroupManager Groups { get; }
    public ILineManager Lines { get; }

    public virtual Task OnConnectedAsync();
    public virtual Task OnReconnectedAsync();
    public virtual Task OnDisconnectedAsync(DisconnectContext disconnect);
}

public sealed class CallerContext
{
    public string LineId { get; }
    public ClaimsPrincipal User { get; }
    public string? UserIdentifier { get; }               // via IUserIdProvider, default Identity.Name
    public IDictionary<object, object?> Items { get; }   // lives as long as the connection (incl. resume)
    public CancellationToken LineClosed { get; }         // fires on final disconnect, not on a drop inside the resume window
    public HttpContext HttpContext { get; }              // the request currently executing (connect or invoke)
    public void HangUp(string? reason = null, bool allowReconnect = false);
}

public interface ISwitchboardClients<T>
{
    T All { get; }
    T AllExcept(params IReadOnlyList<string> lineIds);
    T Caller { get; }        // inside a switchboard only
    T Others { get; }        // inside a switchboard only
    T Line(string lineId);
    T Lines(params IReadOnlyList<string> lineIds);
    T Group(string group);
    T Groups(params IReadOnlyList<string> groups);
    T GroupExcept(string group, params IReadOnlyList<string> excludedLineIds);
    T OthersInGroup(string group);   // inside a switchboard only
    T User(string userId);
    T Users(params IReadOnlyList<string> userIds);
}

public interface ISingleLineProxy : IClientProxy
{
    // Server → one client, awaiting the client's answer (§4.7).
    Task<T> InvokeAsync<T>(string method, object?[] args, CancellationToken cancellationToken);
}

public interface IGroupManager
{
    Task AddToGroupAsync(string lineId, string group, CancellationToken cancellationToken = default);
    Task RemoveFromGroupAsync(string lineId, string group, CancellationToken cancellationToken = default);
    Task<int> RemoveGroupAsync(string group, CancellationToken cancellationToken = default);   // empties it; returns how many lines left
    IReadOnlyCollection<string> GetLines(string group);
}
```

- Groups exist by being joined and disappear when their last line leaves or the group is removed.
  `RemoveGroupAsync` only takes lines out of the group; it does not hang up on them
  (`ILineManager.HangUpGroupAsync` does that).
- **`Items` concurrency.** Calls from one line run one at a time by default
  (`MaximumParallelInvocationsPerLine = 1`, §5.5), so `Items` needs no locking. Raise that limit and
  `Items` is shared between concurrent calls — the docs page must say so where it introduces both.
  `Items` is also touched by `OnDisconnectedAsync`, which never overlaps an invocation on the same
  line: the line's invocation queue is drained or cancelled first.

### 4.4 Calling clients from outside a switchboard — `IOperator`

```csharp
public class OrderShippedHandler(IOperator<ChatBoard, IChatClient> board)
{
    public Task Handle(Order order)
        => board.Clients.User(order.CustomerId).OrderShipped(order.Id);
}
```

- `IOperator<TBoard>` (untyped `IClientProxy`) and `IOperator<TBoard, TClient>` (generated typed
  proxy) are singletons registered by `AddSwitchboard()` + the generated dispatcher registration.
- Exposes `Clients`, `Groups`, `Lines` — everything a switchboard has except `Caller`/`Others`/`Context`.
- Usable from background services, timers, other endpoints, MAUI view models (when the server
  runs on the device).

### 4.5 Hanging up on clients from the server

```csharp
public interface ILineManager
{
    int Count { get; }
    IReadOnlyList<LineInfo> Lines { get; }        // snapshot; safe to enumerate while lines come and go
    LineInfo? Find(string lineId);

    Task<bool> HangUpAsync(string lineId, string? reason = null, bool allowReconnect = false);
    Task<int> HangUpUserAsync(string userId, string? reason = null, bool allowReconnect = false);
    Task<int> HangUpGroupAsync(string group, string? reason = null, bool allowReconnect = false);
    Task HangUpAllAsync(string? reason = null, bool allowReconnect = true);    // shutdown: clients may come back
}
```

```csharp
public sealed record LineInfo(
    string Id,
    string? User,                          // IUserIdProvider result; null for anonymous
    IReadOnlyCollection<string> Groups,    // snapshot
    DateTimeOffset ConnectedAt,            // first connect; unchanged by resumes
    LineStatus Status,                     // Attached | Detached (inside the resume window)
    int Resumes,                           // how many times it has resumed
    string Protocol,                       // "HTTP/1.1" | "HTTP/2" | "HTTP/3" of the current stream
    IPAddress? RemoteAddress
);
```

A server hang-up writes a `close` event, completes the stream, **invalidates the
line token** (so the client cannot resume past a hang-up), and runs `OnDisconnectedAsync` with
`DisconnectReason.HungUp`. Server shutdown uses `allowReconnect: true` and
`DisconnectReason.ServerShutdown` — a MAUI app going to background should not tell its clients to
give up forever.

### 4.6 Disconnect reasons (server side)

```csharp
public enum DisconnectReason
{
    ClientClosed,     // DELETE /connect — the user/app closed it on purpose
    Dropped,          // stream broke and the resume window expired without a resume
    HungUp,           // Context.HangUp / ILineManager.HangUp*
    ServerShutdown,   // server stopping / MAUI lifecycle stop
    Faulted           // unhandled error in the connection pipeline (Exception populated)
}

public sealed record DisconnectContext(DisconnectReason Reason, string? Message, Exception? Exception);
```

This is richer than SignalR's single `Exception?` on purpose — "user logged out" vs "phone went
into a tunnel" is the thing apps actually need to tell apart.

### 4.7 Client results (server → one client, awaiting a value)

```csharp
public class ChatBoard : Switchboard<IChatClient>
{
    public async Task DeleteRoom(string room)
    {
        // Typed: IChatClient declares  Task<bool> ConfirmDelete(string room);
        if (!await Clients.Caller.ConfirmDelete(room))
            throw new SwitchboardException("Cancelled by the user");
        …
    }
}

// Outside a switchboard
var ok = await board.Clients.Line(lineId).InvokeAsync<bool>("ConfirmDelete", [room], ct);
```

- **Only single-line targets** return an `ISingleLineProxy` (`Caller`, `Line(id)`); there
  is no "ask a whole group and aggregate" — same rule as SignalR, and the typed proxy for a group
  target makes `Task<T>` methods a compile-time diagnostic rather than a runtime throw.
  (Single-*user* targets are excluded too: a user may have several connections.)
- **Wire:** the server writes `invoke` with a fresh `invocationId`; the client runs its handler and
  `POST {base}/completion` `{ "invocationId", "result" }` or `{ "invocationId", "error" }`.
- **No deadlock with ordered invocations.** In SignalR, awaiting a client result inside a switchboard method
  deadlocks unless `MaximumParallelInvocationsPerLine > 1`, because the answer queues behind the
  invocation waiting for it. Here the answer arrives on `/completion`, which never enters the
  invocation queue (§5.5), so it works with the default of 1.
- **Pending table:** per connection, `invocationId → TaskCompletionSource`, bounded
  (`MaxPendingClientResults`, default 16).
- **Completes with:**
  - the value — deserialized with `JsonTypeInfo<T>`;
  - `SwitchboardException` carrying the client's error message when the handler threw or no handler is registered;
  - `TimeoutException` after `SwitchboardOptions.ClientResultTimeout` (default 30 s) or the caller's token;
  - `LineClosedException` when the connection closes for good (hang-up, drop past the resume
    window, shutdown).
- **Survives a resume.** A drop inside the resume window does not fail the pending call: the
  `invoke` event is in the replay ring, the client receives it on resume and answers then. The
  client de-duplicates by `invocationId` so a replayed request is never run twice.
- **Late / unknown / duplicate completions** are ignored with 404, never a fault. A completion is
  accepted only on the owning connection's token.
- **Client side:** handlers registered with a return value — `On<TArg, TResult>(…, Func<TArg,
  Task<TResult>>)`. An `invoke` with an `invocationId` and no matching handler is answered with an
  error immediately rather than letting the server wait out the timeout.

## 5. Server internals

### 5.1 Line object

`LineState` (internal): id, token, principal name, groups set, `Items`, replay ring,
outbound `Channel<OutgoingEvent>`, current attached stream (nullable — null while detached inside
the resume window), `CancellationTokenSource` for `ConnectionAborted`, last-activity tick.

### 5.2 Lifecycle state machine

```
          GET /connect (new)                     stream breaks
  (none) ────────────────────▶ Attached ─────────────────────────▶ Detached
                                 ▲  │ OnConnectedAsync                 │  resume window timer
             GET /connect?token  │  │                                  │
             + Last-Event-ID     │  │ close / hang-up / DELETE            │ expires
             OnReconnectedAsync  │  ▼                                  ▼
                                Detached ──────────────────────▶ Closed ── OnDisconnectedAsync(reason)
```

- `OnConnectedAsync` runs **after** `connected` is written, so `Clients.Caller` works inside it.
  The connect handler writes `connected`, then runs `OnConnectedAsync`, then drains.
  A throw from `OnConnectedAsync` closes the connection with `Faulted`.
- Only one stream may be attached at a time. A resume while still attached (client noticed the
  drop before the server did) replaces the old stream — the old writer loop is cancelled.
- A sweeper (lazily started, like MCP's) closes detached connections whose window has elapsed.

### 5.3 Outbound path and backpressure

- All sends to a connection go through its bounded channel; one writer loop per attached stream
  drains it through `ServerSentEventStream`. This serializes writes (the stream is not
  thread-safe) and makes broadcast cost O(enqueue), not O(slowest client).
- Payloads are serialized **once per broadcast**, not once per recipient — the channel carries the
  pre-rendered UTF-8 `data:` bytes.
- Channel full ⇒ the client is not keeping up ⇒ disconnect it with `Dropped` + `allowReconnect:true`
  (configurable: `SlowLinePolicy.HangUp | DropOldest`). Unbounded memory growth on a phone is
  the failure mode to design against.
- While detached, events still go into the replay ring (and the channel is not drained); that is
  what the resume replays.

### 5.4 Resume

- Client reconnects with `?token=` and `Last-Event-ID: n`. If the connection is still Detached and
  the ring still holds `n+1…`, replay them, emit `resumed { missed:false }`, re-attach, run
  `OnReconnectedAsync`. Groups, `Items`, and the line id are preserved.
- Ring overflowed ⇒ `resumed { missed:true }` — the client surfaces this on its `Reconnected`
  event so the app can refetch state.
- Token unknown / expired ⇒ `410 Gone`; the client falls back to a **new** connection (new id)
  and raises `Reconnected` with `NewLine = true`.

### 5.5 Invocation ordering

Independent POSTs can arrive out of order (especially multiplexed on HTTP/2). The client stamps a
per-connection `seq`; with `SwitchboardOptions.MaximumParallelInvocationsPerLine = 1` (the default, as in
SignalR) the server executes invocations in `seq` order, holding a gap for a short timeout. Set it
higher to process concurrently and ignore ordering. Streaming invocations don't hold the queue
once they have started yielding.

### 5.6 Things the transport must get right (CLAUDE.md: every transport and protocol version)

- **HTTP/1.1:** one connection for the stream, invocations on others. Browsers cap six per origin —
  note it in docs; recommend HTTP/2.
- **HTTP/2 / HTTP/3:** stream + invocations multiplexed; tests on both.
- **WebSockets:** not used, explicitly out of scope for the switchboard transport.
- **Tunnels:** each `ITunnelProvider` (relay, SSH, Azure Relay, agent tunnels) must pass a test
  proving events arrive incrementally, not at stream end. `AzureRelay/Http1ResponseReader.cs`
  needs a look for whole-response buffering before promising it.
- **Compression / output caching:** confirm `text/event-stream` is excluded from response
  compression and caching middleware; add exclusions if not.
- **Request timeouts:** `MapSwitchboard` applies `DisableRequestTimeout()` to `GET /connect` only.
- **MAUI lifecycle** (`Shiny.Net.HttpServer.Mobile`): on background-stop, `HangUpAllAsync(allowReconnect: true)`.

### 5.7 Scale-out seam (not built)

Everything above talks to an internal `ISwitchboardLifetimeManager` (send-to-connection/group/user/all,
group membership). v1 ships only the in-process implementation. Keeping the seam lets a later
package add a backplane (Redis, Orleans) without touching switchboards — but no public abstraction is
promised until there is a second implementation to validate it.

## 6. Source generation (server) — `Shiny.Net.HttpServer.Switchboard.SourceGenerators`

Trigger: classes deriving from `Switchboard` / `Switchboard<T>` (non-abstract). For each, emit:

1. **`SwitchboardDispatcher<TBoard>`** — a `switch` on method name that deserializes positional args with
   `JsonTypeInfo<T>` from `JsonTypeInfoRegistry`, resolves `[FromServices]` params from the scope,
   calls the method, and writes the result / stream. Plus authorization metadata per method
   (merged with class-level `[Authorize]`).
2. **Module initializer** registering the dispatcher so `MapSwitchboard<TBoard>` can find it.
3. **For `Switchboard<TClient>`: a `TClient` proxy** — a sealed class implementing the interface where each
   method calls `IClientProxy.SendCoreAsync("Method", [args])` with typed serialization. Also used by
   `IOperator<TBoard, TClient>`.
   - `TClient` rules (diagnostics otherwise): interface only; methods return `Task` (or
     `Task<T>` for client results, §4.7); no properties/events; no generic methods.
   - `Task<T>` methods are only callable through single-line targets: the generator emits
     two proxy shapes (`TClient` for `Caller`/`Line(id)`, and a broadcast shape for the rest
     whose `Task<T>` members report a diagnostic at the call site via an analyzer).
4. **JSON coverage diagnostic:** warn when an argument/return type is not covered by any
   `[JsonSerializable]` in the compilation — the same guarantee the endpoint generator gives.

### 6.1 Contract export — for clients that cannot share an assembly

The typed client (§7.2) wants the two contract interfaces. When the client lives in another repo or
belongs to another team, a shared contracts project is not an option, so the server can publish the
contract as source.

Opt in per switchboard:

```csharp
[ExportContract(BoardInterface = "IChatBoard", Namespace = "Chat.Contracts")]
public class ChatBoard : Switchboard<IChatClient> { … }
```

and set where it lands:

```xml
<PropertyGroup>
  <SwitchboardContractsOutputPath>$(MSBuildProjectDirectory)/../contracts</SwitchboardContractsOutputPath>
</PropertyGroup>
```

The generator emits, per exported switchboard, one self-contained `ChatBoard.Contract.g.cs`:

- `IChatBoard` — derived from the switchboard's public switchboard methods (names after `[LineMethod]`, the
  `CancellationToken`/`[FromServices]`/`CallerContext` parameters stripped). Skipped when the switchboard
  already implements a hand-written contract interface — that one is copied instead.
- `IChatClient` — the `TClient` interface, copied.
- **The DTOs both interfaces mention**, re-emitted from their declaration syntax when they are
  declared in the switchboard's compilation and are plain data (records / classes / enums with no base type
  outside the BCL). Anything else — a type from another package, a type with behavior — gets a
  diagnostic naming it, so the export never silently produces a file that does not compile.
- A `[JsonSerializable]` context covering every contract type (`ChatBoardContractJsonContext`), ready to
  pass to the client's `WithJson(...)`.

A source generator can only add to the compilation, not write arbitrary files, so the copy is done by
a build target shipped in the package's `buildTransitive/*.targets`: it turns on
`EmitCompilerGeneratedFiles` for the `*.Contract.g.cs` outputs and copies them to
`$(SwitchboardContractsOutputPath)` after compile, only when the content changed (so it does not churn
the client repo's git diff). The emitted file is also compiled into the server itself, which makes
the export self-checking: a contract that does not compile breaks the server build first.

## 7. Client — `Shiny.Net.HttpServer.Switchboard.Client`

### 7.1 Untyped API

```csharp
var line = new SwitchboardLineBuilder()
    .WithUrl("https://device.local:5001/chat", o =>
    {
        o.AccessTokenProvider = () => auth.GetTokenAsync();   // Authorization: Bearer on every request
        o.HttpMessageHandlerFactory = h => h;                 // cert pinning, self-signed, etc.
        o.Headers["X-Api-Key"] = "…";
    })
    .WithJson(ChatJsonContext.Default)                        // AOT: all (de)serialization via this
    .WithAutomaticReconnect()                                 // or .WithAutomaticReconnect(IRetryPolicy)
    .Build();

line.On<string, string>("ReceiveMessage", (user, text) => Console.WriteLine($"{user}: {text}"));
line.On<int, string>("Welcome", async (n, s) => { … });                // sync + async overloads
line.On<string, bool>("ConfirmDelete", room => Task.FromResult(true)); // client result (§4.7)

line.StateChanged += (_, e) => …;          // (Previous, Current)
line.Reconnecting += (_, e) => …;          // e.Exception, e.Attempt
line.Reconnected  += (_, e) => …;          // e.LineId, e.NewLine, e.MissedMessages
line.Closed       += (_, e) => …;          // e.Reason (ClientClosed/ServerClosed/HungUp/RetriesExhausted/Faulted), e.Message, e.Exception

await line.StartAsync(ct);

await line.SendAsync("SendMessage", "hello");                  // fire-and-forget semantics, still awaits the 2xx
var online = await line.InvokeAsync<int>("OnlineCount");
await foreach (var tick in line.StreamAsync<Tick>("Ticks", 10).WithCancellation(ct))
    …

await line.StopAsync();                                         // DELETE /connect → server sees ClientClosed
```

- `On(...)` returns `IDisposable` to unregister.
- **State:** `LineState { Disconnected, Connecting, Connected, Reconnecting }`, exposed as
  `State` plus `StateChanged`. `LineId` is null unless connected.
- **Close reasons (client side):**

  | Reason | Cause | Auto-reconnect? |
  | --- | --- | --- |
  | `ClientClosed` | `StopAsync` | no |
  | `ServerClosed` | `close` with `allowReconnect:true` then retries exhausted, or retries not enabled | per policy |
  | `HungUp` | `close` with `allowReconnect:false` | **no, ever** |
  | `RetriesExhausted` | network drop, retry policy returned null | — |
  | `Faulted` | protocol error / unhandled handler exception (configurable) | no |

- **Drop detection:** no bytes (events or heartbeat comments) for `2 × heartbeatMs` ⇒ treat as
  dropped. Relying on TCP to notice is how mobile clients sit "connected" for minutes on a dead link.
- **Reconnect:** resume first (token + `Last-Event-ID`), fall back to a new connection on 410.
  Default `IRetryPolicy`: 0, 2, 10, 30 s then stop (SignalR's default), with jitter.
- **Handlers run** on a single dispatch loop in arrival order (like SignalR); a long handler delays
  the next. Documented; `On(...)` async handlers are awaited.
- **Result handlers run off the loop.** A handler that returns a value (§4.7) is started on its own
  task and the loop moves on; the answer is posted to `/completion` whenever it finishes. A result
  handler is exactly the kind that waits on a person (a confirm dialog), and blocking every other
  message behind it would be wrong. Consequence, documented: a result handler may run concurrently
  with the next ordinary handler.
- **SSE parsing:** a small internal parser over `HttpResponseMessage` content streams — not
  `EventSource`/`SseParser` dependency, so it works identically on MAUI, WASM (fetch streaming via
  `SetBrowserResponseStreamingEnabled`) and desktop. Reuse `System.Net.ServerSentEvents` only if it
  is inbox for every target and AOT-clean — decide in phase 2.
- **Targets:** `net10.0` (inherits `src/Directory.Build.props`); AOT/trim analyzers on.

### 7.2 Strongly typed client — `Shiny.Net.HttpServer.Switchboard.Client.SourceGenerators`

The contract is two interfaces in a project both sides reference:

```csharp
public interface IChatBoard                   // client → server
{
    Task SendMessage(string text);
    Task<int> OnlineCount();
    IAsyncEnumerable<Tick> Ticks(int count, CancellationToken cancellationToken = default);
}

public interface IChatClient                // server → client
{
    Task ReceiveMessage(string user, string text);
    Task UserJoined(string? user);
    Task Welcome(string message);
    Task<bool> ConfirmDelete(string room);  // client result (§4.7)
}
```

Server: `public class ChatBoard : Switchboard<IChatClient>, IChatBoard` — implementing `IChatBoard` is optional,
but when present the server generator verifies every contract method is a switchboard method with a
compatible signature (diagnostic otherwise), so contract drift is a compile error on the server.

Client:

```csharp
[SwitchboardClient<IChatBoard, IChatClient>]
public partial class ChatBoardClient;
```

generates:

```csharp
public partial class ChatBoardClient : IChatBoard, IAsyncDisposable
{
    public ChatBoardClient(SwitchboardLine line);

    public SwitchboardLine Line { get; }                 // state, events, Start/Stop pass through

    // IChatBoard → typed InvokeAsync/StreamAsync
    public Task SendMessage(string text);
    public Task<int> OnlineCount();
    public IAsyncEnumerable<Tick> Ticks(int count, CancellationToken cancellationToken = default);

    // IChatClient → typed events …
    public event Func<string, string, Task>? ReceiveMessage;
    public event Func<string?, Task>? UserJoined;
    public event Func<string, Task>? Welcome;

    // … Task<T> methods: exactly one handler, so a property rather than an event (§4.7)
    public Func<string, Task<bool>>? ConfirmDelete { get; set; }

    // … or bind an implementation in one call
    public IDisposable Register(IChatClient receiver);
}
```

Two receiving styles because they fit different apps: events for a MAUI view model that cares
about two of twelve messages, `Register(IChatClient)` for a service that implements all of them.
Both are generated `On<…>` calls — no reflection. The generator also emits a
`[JsonSerializable]`-coverage diagnostic for contract types missing from the context passed to
`WithJson(...)` when it can see it.

`IChatClient` methods returning `Task<T>` become client-result handlers (§4.7): for those the
generated member is a single-assignment handler property (`Func<string, Task<bool>>? ConfirmDelete`)
rather than an event, because a result has exactly one producer — multicast has no sensible answer.

The contract interfaces come from either a shared project or the server's contract export (§6.1);
the client generator does not care which.

## 8. Phases

Each phase ends green: `dotnet build build.slnf` + `dotnet test`.

### Phase 1 — server core ✅
- [x] Project scaffolding: `.Switchboard`, `.Switchboard.SourceGenerators` (packed into `.Switchboard`), in `build.slnf` / `.slnx`
- [x] Protocol constants + payload DTOs with a `JsonSerializerContext`, plus a built-in primitives context so
      `(string, int)` arguments work without the app listing them
- [x] Line registry, in-process lifetime (`BoardRuntime`), groups, users (`IUserIdProvider`)
- [x] `GET /connect` (new), writer loop, heartbeat, `DELETE /connect`
- [x] `POST /invoke` with token + principal check; results, errors, `SwitchboardException`, detailed-errors switch
- [x] `IAsyncEnumerable<T>` returns streamed as the POST response
- [x] Generator: dispatcher, module initializer, `[Authorize]` / `[LineMethod]` / `[NotALineMethod]`, SWB001–SWB007
- [x] `Switchboard`, `CallerContext`, `ISwitchboardClients<IClientProxy>`, `IGroupManager`, `IOperator<TBoard>`
- [x] `ILineManager` + `Context.HangUp` + `close` event + token invalidation
- [x] Lifecycle callbacks with `DisconnectContext`
- [x] **Pulled forward from phase 2 (server half):** detached state, replay ring, resume, `OnReconnectedAsync`,
      resume-window sweeper, 410 fallback, slow-line backpressure (cut the stream → client resumes)
- [x] Tests: `SwitchboardTests` (23, raw protocol over real sockets + one HTTP/2 in-memory) and
      `SwitchboardGeneratorTests` (8, generator driver) — full suite green

**What phase 1 surfaced:**
- **HTTP/1.1 drop detection is write-driven.** `Http1Connection` only notices a vanished client when a
  write fails; it does not watch for EOF during a long response. A dropped line is therefore detected
  within ~2 heartbeats (≤ 30 s at the 15 s default), and the resume window starts then. Follow-up for
  core, not this package: an EOF watcher on HTTP/1.1 requests with no body left to read — careful
  around pipelining and 101 upgrades, which hand the transport to someone else.
- **Type named like its namespace.** `Shiny.Net.HttpServer.Switchboard.Switchboard` resolves fine from
  app code (`using Shiny.Net.HttpServer.Switchboard;` then `: Switchboard`), but code that itself lives
  under the `Shiny.Net.HttpServer` namespace sees the namespace first (CS0118). Only this repo's tests hit
  it — they put their switchboards in another namespace. Worth one line in the docs.
- **Authorization is enforced by the switchboard itself**, not only by `UseAuthorization`: every method
  shares one `/invoke` route, so method-level `[Authorize]` has to be checked after the target is known,
  and the route's requirements are checked in the same pass. A switchboard marked `[Authorize]` is
  protected even if the app forgot the middleware.
- **The resume token travels in `X-Switchboard-Line`**; `?token=` is accepted only as a fallback for
  clients that cannot set headers on a stream (query strings are logged).

### Phase 2 — .NET client (untyped) ✅
- [x] `.Switchboard.Client`: builder, SSE parser, `On`/`Handle`, `SendAsync`, `InvokeAsync`, `StreamAsync`, `StopAsync`
- [x] State machine, `StateChanged`/`Reconnecting`/`Reconnected`/`Closed`, heartbeat-based drop detection (`ServerTimeout`)
- [x] Seq-ordered invocation: the client numbers calls, the server runs them in order when
      `MaximumParallelInvocationsPerLine` is 1, holding a gap for `InvocationOrderingTimeout`
- [x] A streaming call hands the line's gate and turn back once its headers are out
- [x] End-to-end tests (`SwitchboardClientTests`): resume without loss, new line after the window,
      hang-up ⇒ `HungUp` and no reconnect, close-with-reconnect ⇒ new line, no policy ⇒ `Dropped`,
      silent server ⇒ drop, restart after close, ordering of 25 concurrent calls

### Phase 3 — client results ✅
- [x] `ISingleLineProxy.InvokeAsync<T>` (via `ISwitchboardCallerClients.Caller` / `.Line(id)`), `invocationId`
      on `invoke`, `POST /completion`, pending table bounded by `MaxPendingClientResults`
- [x] `ClientResultTimeout`, caller cancellation, `LineClosedException` on close; completion accepted only
      on the owning token and principal
- [x] Client: result-returning `On`/`Handle<TResult>` run off the dispatch loop; no handler ⇒ immediate
      error; de-dup by `invocationId`
- [x] Tests: from inside a method with one call at a time (no deadlock), from the operator, no handler,
      handler throws, timeout, hang-up while pending, drop + resume while pending (answered once)

### Phase 4 — strongly typed + contract export ✅
- [x] `Switchboard<TClient>` + generated `TClient` proxies; `IOperator<TBoard, TClient>`
- [x] Contract-conformance warning (SWB009)
- [x] `.Switchboard.Client.SourceGenerators`: `[SwitchboardClient<TBoard, TClient>]` (and `<TBoard>`) →
      typed calls, events, result-handler properties, `Register`, `DisposeAsync`
- [x] Contract export: `[ExportContract]`, types rendered from symbols (records keep positional
      constructors, STJ attributes copied, server context's `JsonSourceGenerationOptions` carried over),
      SWB010 for what cannot be exported, `buildTransitive` copy target that writes only on change
- [x] Tests: generator driver tests for every diagnostic, the typed proxy, the typed client, and an
      exported contract compiled against the BCL alone; the sample client builds from nothing but the
      exported file
- [x] Native AOT: both samples publish with no trim/AOT warnings and pass a native-to-native smoke test

### Phase 5 — transport matrix, samples, hardening ✅
- [x] HTTP/1.1 (real sockets), HTTP/2 (in-memory and cleartext on a real socket), the relay tunnel
      (incremental delivery through every hop), resume across a server stop/start
- [x] `text/event-stream` was already excluded from response compression; output caching is opt-in
- [x] Switchboard filters (`ISwitchboardFilter`, `AddSwitchboardFilter<T>`)
- [x] Telemetry: lines active/opened/closed/resumed/overflowed, invocation duration, client results; one span per call
- [x] `samples/Sample.Switchboard` (exports its contract) + `samples/Sample.Switchboard.Client` (uses only the export)

### Phase 6 — the four artifacts ✅
- [x] Docs: `httpserver/switchboard.mdx`, sidebar entry, two rows in the `index.mdx` package table and a
      "read this" row, a pointer from `sse.mdx`; site builds
- [x] Release note under `## 1.5 TBD`
- [x] Skill: Switchboard section (tier 3 / 1 / 2), triggers, description, diagnostics rows
- [x] readme.md: package table + feature row

### Where the build departed from the plan
- **`Task<T>` on a broadcast target** throws `NotSupportedException` at run time rather than being an
  analyzer diagnostic at the call site: the generated proxy cannot know its target statically.
- **No `.Mobile` change was needed.** `HttpServer.StopAsync` cancels every in-flight request, so streams
  end, lines detach, and clients resume when the server restarts within the window (tested).
- **Client close reasons gained `Dropped`** (drop with no retry policy) beside the planned ones.
- **Contract export always derives the board interface** from the switchboard's methods (with a
  `CancellationToken` on each); it does not copy a hand-written contract interface.
- **HTTP/3, SSH, Azure Relay and agent tunnels** have no end-to-end switchboard test. HTTP/3 needs QUIC
  on the test host; the relay test covers the tunnel mechanism they share.
- **HTTP/1.1 drop detection** is still write-driven (≤ ~2 heartbeats); the core EOF watcher remains a
  follow-up for `Http1Connection`.

### After v1
- Client → server streaming: `IAsyncEnumerable<T>` switchboard parameters, sent as a chunked NDJSON request
  body (the .NET client can do this on HTTP/1.1 chunked and HTTP/2).
- A backplane behind the `ISwitchboardLifetimeManager` seam (§5.7), once there is a concrete need.

## 9. Open decisions

None blocking. Revisit during phase 2: whether to use `System.Net.ServerSentEvents` for client-side
parsing or keep an internal parser (§7.1).

---

## Appendix A — a complete example

A chat app in three projects, written against the API above. Its job is to show that the API holds
together end to end; it becomes `samples/Sample.Switchboard` in phase 5.

### A.1 Contracts (`Chat.Contracts`, referenced by both sides)

```csharp
using System.Text.Json.Serialization;

namespace Chat.Contracts;

public record ChatMessage(string Room, string User, string Text, DateTimeOffset At);
public record RoomInfo(string Name, int Members);

// client → server
public interface IChatBoard
{
    Task JoinRoom(string room);
    Task LeaveRoom(string room);
    Task SendMessage(string room, string text);
    Task<IReadOnlyList<RoomInfo>> ListRooms();
    Task DeleteRoom(string room);                                     // admin only (server enforces)
    IAsyncEnumerable<ChatMessage> History(string room, int take, CancellationToken cancellationToken = default);
}

// server → client
public interface IChatClient
{
    Task MessageReceived(ChatMessage message);
    Task UserJoined(string room, string user);
    Task UserLeft(string room, string user);
    Task Notice(string text);
    Task<bool> ConfirmDelete(string room);                            // client result
}

[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(RoomInfo))]
[JsonSerializable(typeof(IReadOnlyList<RoomInfo>))]
public partial class ChatJsonContext : JsonSerializerContext;
```

If the client cannot reference this project, delete it and put
`[ExportContract(Namespace = "Chat.Contracts")]` on `ChatBoard` instead (§6.1) — the server then
generates this same file.

### A.2 Server (`Chat.Server`)

**Program.cs**

```csharp
using Chat.Contracts;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Switchboard;

JsonTypeInfoRegistry.Register(ChatJsonContext.Default);

var builder = HttpServer.CreateBuilder();
builder.Options.Port = 5001;

builder.AddAuthentication().AddJwtBearer(o =>
{
    o.Issuer = "chat";
    o.Audience = "chat";
    o.SigningKey = JwtSigningKey.FromSecret(builder.Configuration["Jwt:Key"]!);
});

builder.AddSwitchboard(o =>
{
    o.HeartbeatInterval = TimeSpan.FromSeconds(15);
    o.ResumeWindow = TimeSpan.FromSeconds(30);     // dropped lines keep their id, groups and missed messages
    o.ReplayBufferSize = 256;
    o.ClientResultTimeout = TimeSpan.FromSeconds(60);
    o.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

builder.Services.AddSingleton<IRoomStore, InMemoryRoomStore>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapSwitchboard<ChatBoard>("/chat").RequireAuthorization();
app.MapChatServerEndpoints();                      // generated from the [Route] class below

await app.RunAsync();
```

**ChatBoard.cs**

```csharp
using System.Runtime.CompilerServices;
using Chat.Contracts;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer.Switchboard;

namespace Chat.Server;

public class ChatBoard(IRoomStore rooms, ILogger<ChatBoard> logger) : Switchboard<IChatClient>, IChatBoard
{
    string Me => Context.UserIdentifier!;

    // Per-line state; survives a resume. Calls from one line run one at a time by default, so no lock.
    HashSet<string> JoinedRooms
    {
        get
        {
            if (!Context.Items.TryGetValue("rooms", out var value))
                Context.Items["rooms"] = value = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return (HashSet<string>)value!;
        }
    }

    // ---- lifecycle ----

    public override async Task OnConnectedAsync()
    {
        logger.LogInformation("{User} connected on line {LineId}", Me, Context.LineId);
        await Clients.Caller.Notice($"Welcome, {Me}");
    }

    public override Task OnReconnectedAsync()
    {
        // Same line id, same groups; missed messages were already replayed.
        logger.LogInformation("{User} resumed line {LineId}", Me, Context.LineId);
        return Task.CompletedTask;
    }

    public override async Task OnDisconnectedAsync(DisconnectContext disconnect)
    {
        foreach (var room in JoinedRooms)
        {
            rooms.Leave(room, Me);
            await Clients.OthersInGroup(room).UserLeft(room, Me);
        }

        switch (disconnect.Reason)
        {
            case DisconnectReason.ClientClosed:
                logger.LogInformation("{User} signed off", Me);
                break;
            case DisconnectReason.Dropped:
                logger.LogWarning("{User} dropped and never came back", Me);
                break;
            case DisconnectReason.HungUp:
                logger.LogInformation("Hung up on {User}: {Message}", Me, disconnect.Message);
                break;
            case DisconnectReason.Faulted:
                logger.LogError(disconnect.Exception, "{User}'s line faulted", Me);
                break;
        }
    }

    // ---- IChatBoard ----

    public async Task JoinRoom(string room)
    {
        if (!JoinedRooms.Add(room))
            return;

        await Groups.AddToGroupAsync(Context.LineId, room);
        rooms.Join(room, Me);
        await Clients.OthersInGroup(room).UserJoined(room, Me);
    }

    public async Task LeaveRoom(string room)
    {
        if (!JoinedRooms.Remove(room))
            return;

        await Groups.RemoveFromGroupAsync(Context.LineId, room);
        rooms.Leave(room, Me);
        await Clients.Group(room).UserLeft(room, Me);
    }

    public async Task SendMessage(string room, string text)
    {
        if (!JoinedRooms.Contains(room))
            throw new SwitchboardException($"Join '{room}' before posting to it");   // always reaches the client

        var message = new ChatMessage(room, Me, text, DateTimeOffset.UtcNow);
        await rooms.AddAsync(message);
        await Clients.Group(room).MessageReceived(message);
    }

    public Task<IReadOnlyList<RoomInfo>> ListRooms() => rooms.ListAsync();

    // Streamed back as an event stream; stops when the client stops reading.
    public async IAsyncEnumerable<ChatMessage> History(
        string room,
        int take,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in rooms.ReadAsync(room, take, cancellationToken))
            yield return message;
    }

    [Authorize(Roles = "admin")]                                        // non-admins: 403 → exception on the client
    public async Task DeleteRoom(string room)
    {
        // Client result: ask the caller and wait for the answer.
        if (!await Clients.Caller.ConfirmDelete(room))
            throw new SwitchboardException("Delete cancelled");

        await Clients.Group(room).Notice($"'{room}' has been deleted");
        await Groups.RemoveGroupAsync(room);                            // everyone out; nobody hung up on
        await rooms.DeleteAsync(room);
    }
}
```

**AdminEndpoints.cs** — the operator: reaching lines from outside the switchboard, and hanging up

```csharp
using Chat.Contracts;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Switchboard;

namespace Chat.Server;

public record Announcement(string Text);

[Route("/admin")]
[Authorize(Roles = "admin")]
public class AdminEndpoints(IOperator<ChatBoard, IChatClient> board)
{
    [Get("/lines")]
    public IActionResult List() => Results.Ok(board.Lines.Lines);

    [Post("/announce")]
    public async Task<IActionResult> Announce(Announcement body)
    {
        await board.Clients.All.Notice(body.Text);
        return Results.NoContent();
    }

    [Post("/rooms/{room}/announce")]
    public async Task<IActionResult> AnnounceToRoom(string room, Announcement body)
    {
        await board.Clients.Group(room).Notice(body.Text);
        return Results.NoContent();
    }

    // One line. The client gets Closed(HungUp) and does not reconnect.
    [Post("/lines/{lineId}/hang-up")]
    public async Task<IActionResult> HangUp(string lineId, string? reason)
        => await board.Lines.HangUpAsync(lineId, reason ?? "Removed by a moderator")
            ? Results.NoContent()
            : Results.NotFound();

    // Every device a user is on: phone, tablet, laptop.
    [Post("/users/{user}/ban")]
    public async Task<IActionResult> Ban(string user)
    {
        var count = await board.Lines.HangUpUserAsync(user, "You have been banned");
        return Results.Ok(new { hungUp = count });
    }
}
```

### A.3 Client (`Chat.Console`, references `Chat.Contracts`)

**ChatBoardClient.cs** — the whole file; the generator writes the rest

```csharp
using Chat.Contracts;
using Shiny.Net.HttpServer.Switchboard.Client;

[SwitchboardClient<IChatBoard, IChatClient>]
public partial class ChatBoardClient;
```

**Program.cs**

```csharp
using Chat.Contracts;
using Shiny.Net.HttpServer.Switchboard.Client;

var token = args[0];
var currentRoom = "general";
TaskCompletionSource<bool>? pendingConfirm = null;

var line = new SwitchboardLineBuilder()
    .WithUrl("https://localhost:5001/chat", o =>
    {
        o.AccessTokenProvider = () => Task.FromResult<string?>(token);
    })
    .WithJson(ChatJsonContext.Default)
    .WithAutomaticReconnect()                      // 0s, 2s, 10s, 30s (with jitter), then give up
    .Build();

await using var chat = new ChatBoardClient(line);

// ---- line state ----

line.StateChanged += (_, e) => Log($"[{e.Previous} → {e.Current}]");

line.Reconnecting += (_, e) => Log($"Line dropped ({e.Exception?.Message}); retry #{e.Attempt}…");

line.Reconnected += async (_, e) =>
{
    if (e.NewLine)
    {
        // Could not resume (server restarted, or gone too long): new line id, no groups.
        Log("Reconnected on a new line; rejoining…");
        await chat.JoinRoom(currentRoom);
    }
    else if (e.MissedMessages)
    {
        Log("Reconnected, but some messages were lost; reloading history…");
        await PrintHistory();
    }
    else
    {
        Log("Reconnected; nothing missed.");
    }
};

line.Closed += (_, e) => Log(e.Reason switch
{
    LineCloseReason.ClientClosed     => "Signed off.",
    LineCloseReason.HungUp           => $"The server hung up: {e.Message}",
    LineCloseReason.ServerClosed     => $"Server closed the line: {e.Message}",
    LineCloseReason.RetriesExhausted => "Could not reconnect. Giving up.",
    LineCloseReason.Faulted          => $"Line failed: {e.Exception?.Message}",
    _                                => e.Reason.ToString()
});

// ---- server → client ----

chat.MessageReceived += m => { Log($"[{m.Room}] {m.User}: {m.Text}"); return Task.CompletedTask; };
chat.UserJoined      += (room, user) => { Log($"* {user} joined {room}"); return Task.CompletedTask; };
chat.UserLeft        += (room, user) => { Log($"* {user} left {room}");   return Task.CompletedTask; };
chat.Notice          += text => { Log($"! {text}"); return Task.CompletedTask; };

// Client result: exactly one handler; its value goes back to the server. It runs off the message
// loop (§7.1), so other messages keep arriving while this waits for the user's next input line.
chat.ConfirmDelete = room =>
{
    Log($"Delete '{room}' and all its history? [y/N]");
    pendingConfirm = new TaskCompletionSource<bool>();
    return pendingConfirm.Task;
};

// ---- go ----

await line.StartAsync();
Log($"Connected on line {line.LineId}");

await chat.JoinRoom(currentRoom);

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
                await line.StopAsync();            // server sees DisconnectReason.ClientClosed
                return;

            case ["/join", var room]:
                await chat.LeaveRoom(currentRoom);
                currentRoom = room;
                await chat.JoinRoom(room);
                await PrintHistory();
                break;

            case ["/delete", var room]:
                _ = DeleteRoom(room);              // not awaited: the server will ask us to confirm mid-call
                break;

            default:
                await chat.SendMessage(currentRoom, input);
                break;
        }
    }
    catch (SwitchboardException ex)                // server-side SwitchboardException, or 403 from [Authorize]
    {
        Log($"x {ex.Message}");
    }
}

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
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await foreach (var m in chat.History(currentRoom, 20, cts.Token))
        Log($"  [{m.At:t}] {m.User}: {m.Text}");
}

static void Log(string text) => Console.WriteLine(text);
```

**Without the generator**, for comparison:

```csharp
line.On<ChatMessage>("MessageReceived", m => Log($"[{m.Room}] {m.User}: {m.Text}"));
line.On<string, bool>("ConfirmDelete", room => Task.FromResult(true));

await line.InvokeAsync("JoinRoom", "general");
var rooms = await line.InvokeAsync<IReadOnlyList<RoomInfo>>("ListRooms");
await foreach (var m in line.StreamAsync<ChatMessage>("History", "general", 20))
    Log(m.Text);
```

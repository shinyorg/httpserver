---
name: shiny-httpserver
description: Generate code using Shiny.Net.HttpServer — a dependency-light, AOT/trim-clean HTTP/1.1, HTTP/2 & HTTP/3 server that runs anywhere .NET runs, including .NET MAUI, where ASP.NET Core cannot. Covers routing, middleware, source-generated typed endpoints, results and JSON, static files and Blazor WASM, uploads/downloads, WebSockets, SSE, sessions, OpenAPI, authentication (Basic/API key/cookie/JWT), authorization, CORS, rate limiting, IP filtering, TLS and self-signed certificates, tunnelling (relay, SSH, quick tunnels, Azure Relay) and hosting an MCP server.
auto_invoke: true
triggers:
- Shiny.Net.HttpServer
- HttpServer
- embedded http server
- http server in MAUI
- web server on device
- HttpServerOptions
- HttpServerBuilder
- AddHttpServer
- OnGet
- OnRequest
- MapGet
- IHttpMiddleware
- RequestDelegate
- HttpContext
- RouteAttribute
- IHttpEndpoint
- IEndpointModule
- MapMyAppEndpoints
- FromRoute
- FromQuery
- FromBody
- FromServices
- IActionResult
- Results.Ok
- JsonTypeInfoRegistry
- ProblemDetails
- UseStaticFiles
- UseEmbeddedFiles
- UseBlazorWebAssembly
- MapFileBrowser
- FileDownloadResult
- ReadMultipartAsync
- AcceptWebSocketAsync
- SendEventsAsync
- ServerSentEvents
- UseSessions
- ISession
- MapOpenApi
- AddAuthentication
- AddAuthorization
- AddBasic
- AddApiKey
- AddCookie
- AddJwtBearer
- JwtSigningKey
- JwtTokenGenerator
- UseCors
- UseRateLimiter
- UseIpFilter
- ServerCertificate
- CertificatePinning
- ITunnelProvider
- RunTunnelAsync
- RelayTunnelProvider
- RelayServer
- AddSshTunnel
- QuickTunnel
- AddQuickTunnel
- AddAzureRelayTunnel
- MapMcp
- Shiny.Net.HttpServer.Mcp
- SWS001
- SWS006
---

# Shiny HTTP Server Skill

## Triggers
- Shiny.Net.HttpServer
- embedded / in-app HTTP server
- HTTP server in a .NET MAUI app
- serving a web UI or API from a device
- tunnelling a device server to the internet
- hosting an MCP server without ASP.NET Core

You are an expert in `Shiny.Net.HttpServer`, a dependency-light HTTP server for .NET.

## When to Use This Skill

Invoke this skill when the user wants to:
- Serve HTTP from an app that cannot use ASP.NET Core (.NET MAUI, single-file, embedded, AOT)
- Add routes, middleware or typed endpoints to a `Shiny.Net.HttpServer` app
- Serve static files, a Blazor WebAssembly app, or a device's file system over HTTP
- Add authentication, authorization, CORS, rate limiting or IP filtering to that server
- Make a device-local server reachable from the internet through a tunnel
- Host a Model Context Protocol server inside a non-ASP.NET app

**Do not** use this skill for ASP.NET Core / Kestrel / minimal APIs. Those are a different library
with similar-looking names.

## Library Overview

**Documentation**: https://shinylib.net/httpserver

Only `Microsoft.Extensions.*` abstractions are taken as dependencies. Everything else — JSON, crypto,
JWT, OpenAPI, HPACK, QPACK — is in the box. Everything targets `net10.0` with the trim, AOT and
single-file analyzers on.

**The single hard rule: nothing is discovered by reflection.** Routes and binders are generated at
compile time; JSON goes through `JsonTypeInfo` from a `JsonSerializerContext`. Any code you generate
must hold that line, or it fails on a trimmed device build.

### Packages

```bash
dotnet add package Shiny.Net.HttpServer                  # the server
dotnet add package Shiny.Net.HttpServer.SourceGenerators # typed endpoints (analyzer)
dotnet add package Shiny.Net.HttpServer.Jwt              # JWT auth
dotnet add package Shiny.Net.HttpServer.Ssh              # SSH + quick tunnels
dotnet add package Shiny.Net.HttpServer.AzureRelay       # Azure Relay tunnel (NOT AOT-clean)
dotnet add package Shiny.Net.HttpServer.Mcp              # Model Context Protocol transport
```

## The four tiers — the spine of this library

Every new API belongs to one of these. Say which when you introduce one. They compose in one app.

| Tier | What it is | Use when |
| --- | --- | --- |
| 0 | `OnRequest(ctx => …)` — one delegate, no routing | A single handler, a test fixture, a fallback |
| 1 | `OnGet`/`OnPost`/… — raw handlers behind a route template | A handful of routes, no binding wanted |
| 2 | `Use(...)` / `IHttpMiddleware` — the pipeline | Cross-cutting work |
| 3 | `[Route]` classes + the source generator | Anything real: typed parameters, DI, OpenAPI |

**Default to tier 3** when the user has more than a couple of endpoints or wants typed parameters.
Default to tier 1 for small, script-like servers. Never suggest reflection-based alternatives.

## Setup

Choose the host shape from who owns the container:

```csharp
// (a) No container — smallest possible
var server = new HttpServer(new HttpServerOptions { Port = 8080 });
server.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong"));
await server.RunAsync();

// (b) The builder — a console app or service that wants DI
var builder = HttpServer.CreateBuilder();
builder.Configure(o => o.Port = 8080);
builder.Services.AddSingleton<IWidgetStore, WidgetStore>();
var app = builder.Build();
app.MapMyAppEndpoints();
await app.RunAsync();

// (c) An existing container — MAUI, generic host
builder.Services.AddHttpServer(
    o => { o.Address = IPAddress.Any; o.Port = 0; },
    server => server.MapMyAppEndpoints(),
    autoStart: false     // for an app with a "share" toggle
);
```

`autoStart: false` + `server.StartAsync()` from the UI is the right shape for MAUI. `Port = 0` lets
the OS pick; read it back from `server.ListenUrl`.

### Defaults worth knowing

- Binds **loopback** by default. Set `Address = IPAddress.Any` for LAN access — deliberately.
- `Limits.MaxRequestBodySize` is 30 MB; raise it for uploads.
- `HideExceptionDetails` is on; turn it off in development only.

## Tier 3: typed endpoints (preferred)

```csharp
[Route("/api/widgets")]
public class WidgetEndpoints(IWidgetStore store, ILogger<WidgetEndpoints> logger)
{
    /// <summary>Fetches a widget.</summary>          // becomes the OpenAPI summary
    [Get("/{id:int}")]
    [Produces(200, typeof(Widget))]
    [Produces(404)]
    public async Task<IActionResult> GetWidget(int id, CancellationToken ct)
        => await store.FindAsync(id, ct) is { } w ? new OkObjectResult(w) : new NotFoundResult();

    [Get]
    public async Task<IReadOnlyList<Widget>> List(int take = 10, string? search = null,
        CancellationToken ct = default) => await store.ListAsync(take, search, ct);

    [Post]
    public async Task<IActionResult> Create(CreateWidget request, CancellationToken ct)
        => new CreatedResult($"/api/widgets/{(await store.AddAsync(request.Name, ct)).Id}");
}

app.MapWidgetEndpoints();     // one class
app.MapMyAppEndpoints();      // every [Route] class in the assembly (name = assembly name)
```

Rules to follow when generating endpoint classes:

1. Use **primary constructors** for dependencies — they are resolved from the request scope.
2. Class must be `public` or `internal`, non-static, non-abstract, non-generic (SWS004).
3. Verb attributes: `[Get]`, `[Post]`, `[Put]`, `[Delete]`, `[Patch]`, or `[HttpMethod("VERB", "/t")]`.
4. `[NonEndpoint]` excludes a public method.
5. Always accept and pass `CancellationToken`.

### Binding conventions (do not add attributes when the convention already fits)

In order: ambient types → route token → query → JSON body → container.

- `HttpContext`, `HttpRequest`, `HttpResponse`, `CancellationToken` are handed over directly.
- A parameter whose name matches a route token **and** whose type is `IParsable` binds from the route.
- Anything else `IParsable` (plus enums, nullables, and arrays of those) binds from the query.
- A complex type on a body-carrying verb binds from JSON — **at most one per method** (SWS007).
- Everything else comes from the container.

Overrides: `[FromRoute]`, `[FromQuery]`, `[FromHeader]`, `[FromBody]`, `[FromServices]`, each with an
optional `Name`.

A default value makes a parameter optional. Bind failures are 400s naming the parameter and type,
raised before the method is called.

### Return types

| Return | Response |
| --- | --- |
| `void` / `Task` / `ValueTask` | Nothing — you wrote the response yourself |
| `IResult` / `IActionResult` | Executed |
| `string` | `text/plain` |
| Anything else | JSON from compile-time metadata |

All may be wrapped in `Task<T>`/`ValueTask<T>`. Anything else is SWS003.

### One endpoint per class

```csharp
[Get("/health/{component}")]
public class HealthEndpoint(IHealthChecks checks) : IHttpEndpoint
{
    public async Task<IActionResult> HandleAsync(string component, CancellationToken ct)
        => await checks.RunAsync(component, ct) ? new OkResult() : new StatusCodeResult(503);
}
```

Exactly one public `Handle`/`HandleAsync` (SWS010) and a verb attribute on the class (SWS011).

### Runtime-mounted route groups

```csharp
public sealed class AdminModule : IEndpointModule
{
    public void Map(IEndpointRouteBuilder endpoints)
        => endpoints.MapPost("/admin/reset", ctx => …).RequireAuthorization("admin");
}

app.MapModule(new AdminModule());
app.UnmapModule<AdminModule>();
```

## JSON — the AOT rule

**Always** declare a `JsonSerializerContext` covering every type that crosses an endpoint boundary:

```csharp
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Widget))]
[JsonSerializable(typeof(IReadOnlyList<Widget>))]
[JsonSerializable(typeof(CreateWidget))]
public partial class AppJson : JsonSerializerContext;
```

- With the **source generator** referenced, registration is emitted for you (a module initializer),
  and a missing type is build warning **SWS006**.
- Without it, register by hand: `JsonTypeInfoRegistry.Register(AppJson.Default);`
- Never generate `Results.Json(value, options)` (the reflection overload) — it is
  `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`.
- Do not try to emit the context from a generator: generators cannot see each other's output. The app
  owns it.

Reading a body from a raw handler: `await ctx.Request.ReadJsonAsync(AppJson.Default.NewNote)`
(returns `null` for absent/malformed — return a 400, do not throw).

## Results

`Results.X()` and the MVC-shaped types are the same objects: `Results.NotFound()` ≡
`new NotFoundResult()`. Mix freely; prefer `IActionResult` types inside endpoint classes and
`Results.*` in raw handlers.

Common: `Ok()`, `Ok(value)`, `Created(location, value)`, `NoContent()`, `BadRequest(message)`,
`Unauthorized()`, `Forbidden()`, `NotFound()`, `Conflict()`, `StatusCode(n)`, `Text`, `Bytes`,
`Stream`, `File`, `Redirect`, `Json`, `Negotiate`, `Problem`, `ValidationProblem`,
`ServerSentEvents`.

## Tier 2: middleware

```csharp
app.Use(async (ctx, next) => { /* before */ await next(ctx); /* after */ });

public sealed class ApiKeyMiddleware(IKeyStore keys) : IHttpMiddleware
{
    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next) { … }
}
builder.Services.AddSingleton<ApiKeyMiddleware>();
app.Use<ApiKeyMiddleware>();          // resolved per request from the request scope
```

`UseAfterRouting(...)` runs after endpoint selection, so `ctx.Endpoint` (and its metadata) is
populated.

### Ordering — generate this order

```csharp
app.UseCors();                  // preflights carry no credentials
app.UseRateLimiter();           // before routing: a throttled request should cost nothing
app.UseIpFilter();
app.UseResponseCompression();
app.UseAuthentication();        // before routing
app.UseAuthorization();         // after routing (registers itself as after-routing)
app.UseSessions();
app.UseStaticFiles("./wwwroot");
```

**Critical gotchas:**

- The pipeline is composed **once**, at first serve. Registering middleware after the server starts
  throws; `RestartAsync` does not recompose it. Routes *can* change at any time.
- Headers flush on the first body write. To add a header around a handler, use
  `ctx.Response.OnStarting(...)`, not code after `await next(ctx)`.
- `HttpContext` is **pooled** — never capture it past the end of the handler.
- **Static files are served before routing**, so `[Authorize]`/`RequireAuthorization` (after routing)
  does *not* protect them. Put an authentication check middleware in front of `UseStaticFiles` /
  `UseEmbeddedFiles` when the served content is not public.

## Security

```csharp
builder.Services.AddAuthentication()
    .AddJwtBearer(o => { o.Issuer = "app"; o.Audience = "app"; o.SigningKey = key; });
    // or .AddBasic(o => o.AddUser("ada", pw, "admin")) / .AddBasic<UserStore>()
    // or .AddApiKey(o => o.AddKey(key, "ci", "deploy"))
    // or .AddCookie(o => { o.Protector = new TicketProtector(k); o.LoginPath = "/login"; })

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("admin", p => p.RequireRole("admin"));
    // o.SetFallbackPolicy(p => p.RequireAuthenticatedUser());   // deny by default
});
```

- `[Authorize]` / `[AllowAnonymous]` on endpoint classes and methods; `RequireAuthorization(...)` /
  `AllowAnonymous()` on raw routes (they apply to the **most recently mapped** route).
- A method's `[Authorize]` **adds to** the class's; `[AllowAnonymous]` always wins.
- 401 = anonymous, 403 = authenticated but not permitted. Never put the denial reason in the body.
- JWT: `JwtSigningKey.FromSecret/FromRsa/FromEcdsa`; `AddJwtBearer` also registers a
  `JwtTokenGenerator` — inject it in a login endpoint so issuing and validating cannot drift.
  Never generate a key at startup in production code (it invalidates every issued token on restart).
- Basic auth **refuses plain HTTP** off loopback. That is intentional; do not work around it.

CORS / rate limiting / IP filtering all follow the same shape — inline policy, or named policies plus
`RequireX("name")` / `DisableX()` on routes and `[EnableCors]`, `[EnableRateLimiting]`,
`[RequireIpFilter]` (+ `[DisableCors]`, `[DisableRateLimiting]`, `[AllowAnyIp]`) on endpoints.

## Content

```csharp
app.UseStaticFiles("./wwwroot", o => o.FallbackFile = "index.html");
app.UseEmbeddedFiles(typeof(App).Assembly, "MyApp.wwwroot");     // packaged / MAUI
app.UseBlazorWebAssembly("./wwwroot");                            // SPA + precompressed + cache policy
app.MapFileBrowser("/files", o => o.RootPath = FileSystem.AppDataDirectory).RequireAuthorization();
```

- Unknown file extensions are **not served** by default. Add `ContentTypeOverrides[".x"]` rather than
  turning on `ServeUnknownFileTypes`.
- Uploads: `await foreach (var part in ctx.Request.ReadMultipartAsync(ct))` and
  `part.SafeFileName()` (never `part.FileName` — traversal). `ReadFormAsync` buffers; use it only for
  small fields.
- Downloads: `FileDownloadResult.FromFile(...)` gives ranges, ETags and conditional GETs.

## Realtime

```csharp
// WebSockets — the handler owns the socket; loop inside it
app.OnGet("/ws", async ctx =>
{
    if (!ctx.Request.IsWebSocketRequest()) { ctx.Response.StatusCode = 400; return; }

    await using var socket = await ctx.AcceptWebSocketAsync();
    while (await socket.ReceiveAsync(ctx.RequestAborted) is { } msg)
        await socket.SendAsync(msg.Text, ctx.RequestAborted);
});

// SSE
app.OnGet("/events", ctx => ctx.SendEventsAsync(async stream =>
{
    while (!stream.Aborted.IsCancellationRequested)
    {
        await stream.SendAsync($"tick {DateTime.UtcNow:O}");
        await Task.Delay(1000, stream.Aborted);
    }
}));
```

## Tunnelling

```csharp
// Zero-account public HTTPS from a phone
builder.Services.AddQuickTunnel(QuickTunnelHost.Sish, subdomain: "my-device");
// then, from a button: await tunnel.StartAsync();

// A host you own
builder.Services.AddSshTunnel(o =>
{
    o.Host = "tunnel.example.com"; o.Username = "tunnel"; o.PrivateKeyPath = keyPath;
    o.RemoteBindAddress = "0.0.0.0"; o.RemotePort = 8080;
    o.HostKeyFingerprints.Add("SHA256:…");     // required unless AcceptAnyHostKey
});

// Any provider, manually
await app.RunTunnelAsync(provider, logger, ct);
```

- `QuickTunnel` is `INotifyPropertyChanged` — **bind** to `PublicUrl`, never read it once: a free
  tunnel reassigns the address on every reconnect. Its events fire on a background thread; marshal to
  the UI thread in MAUI.
- Always put authentication in front of anything exposed by a tunnel.
- `AzureRelay` is deliberately **not** AOT-clean; do not suggest it for a trimmed/AOT app.
- There is no ngrok/Cloudflare provider by design — they need an agent process, impossible on
  iOS/Android.

## MCP

```csharp
builder.Services
    .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "device", Version = "1.0.0" })
    .WithTools<DeviceTools>(AppJson.Default.Options)   // pass the context — see below
    .WithHttpTransport(o => { o.MaxSessions = 8; o.IdleSessionTimeout = TimeSpan.FromMinutes(10); });

app.MapMcp();          // POST/GET/DELETE/OPTIONS on /mcp
```

- A tool's parameter and return types are published as a JSON schema, and building that by reflection
  does not survive trimming. Tools using only primitives need nothing; anything richer must pass a
  `JsonSerializerContext`'s options to `WithTools<T>()`. `MapMcp()` throws at startup naming the type
  if you miss one.
- Never generate `WithToolsFromAssembly()` / `WithPromptsFromAssembly()` / the `IEnumerable<Type>`
  overloads — they scan at runtime and are `RequiresUnreferencedCode`.
- `AllowedOrigins` is empty by default and should stay that way unless a browser client needs it.

## MAUI specifics

- iOS: `NSLocalNetworkUsageDescription` in `Info.plist` — without it the app is denied silently.
- Mac Catalyst: `com.apple.security.network.server` entitlement — without it the bind is refused.
- Android: `android.permission.INTERNET`.
- iOS suspends the app in the background; the server stops answering.
- Prefer plain HTTP on the LAN; terminate TLS at a tunnel. A self-signed certificate needs per-device
  trust (`ServerCertificate.Create()` / `CreateOrLoad(path)`, plus
  `CertificatePinning.CreateHandler(cert)` for the app's own `HttpClient`).

## Build diagnostics (source generator)

| Code | Meaning |
| --- | --- |
| SWS001 | Invalid route template |
| SWS002 | Parameter cannot be bound |
| SWS003 | Unsupported return type |
| SWS004 | Endpoint class/method not reachable from generated code |
| SWS005 | Duplicate route in the assembly |
| SWS006 | **Warning** — type crosses an endpoint boundary but no `JsonSerializerContext` declares it |
| SWS007 | More than one body parameter |
| SWS008 | `[FromRoute]` names a token the template lacks |
| SWS009 | **Warning** — template captures a token no parameter receives |
| SWS010 | `IHttpEndpoint` without a single `Handle`/`HandleAsync` |
| SWS011 | `IHttpEndpoint` without a verb attribute on the class |

## Best Practices

1. **Prefer tier 3** for anything with typed parameters; tier 1 for small servers.
2. **Always declare a `JsonSerializerContext`** and never use the reflection JSON overloads.
3. **Route constraints are a closed set** — `int`, `long`, `guid`, `bool`, `double`, `decimal`,
   `alpha`, `minlength(n)`, `maxlength(n)`, `length(n)`. Validate anything else in the handler so it
   can return a meaningful error instead of a 404.
4. **Segments are literal or a parameter, never mixed** (`v{version}` is rejected at registration).
5. **Register middleware before starting**; register routes whenever you like.
6. **Put auth in front of static files** when they are not public.
7. **Pass `CancellationToken`** (or `ctx.RequestAborted`) into everything async.
8. **Never capture `HttpContext`** past the handler — contexts are pooled.
9. **Loopback by default** — only bind `IPAddress.Any` when the user asked for network access.
10. **A tunnel is the public internet.** Authentication and rate limiting first, always.

---
name: shiny-httpserver
description: Generate code using Shiny.Net.HttpServer — a dependency-light, AOT/trim-clean HTTP/1.1, HTTP/2 & HTTP/3 server that runs anywhere .NET runs, including .NET MAUI, where ASP.NET Core cannot. Covers routing, middleware, source-generated typed endpoints, results and JSON, content negotiation with XML/MessagePack/protobuf formatters in both directions, static files and Blazor WASM, uploads/downloads, WebSockets, SSE, sessions, OpenAPI, authentication (Basic/API key/cookie/JWT), authorization, CORS, rate limiting, IP filtering, TLS and self-signed certificates, tunnelling (relay, SSH, quick tunnels, Azure Relay), serving a directory over WebDAV, serving gRPC and gRPC-Web, and hosting an MCP server.
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
- AddContentNegotiation
- ContentNegotiationOptions
- NegotiateByDefault
- IOutputFormatter
- IInputFormatter
- InputFormatterResult
- BodyReadStatus
- TryReadBodyAsync
- AddXml
- XmlOutputFormatter
- XmlInputFormatter
- AddMessagePack
- MessagePackOutputFormatter
- AddProtobuf
- BinaryCodecRegistry
- AddBinaryFormat
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
- QuickTunnelHost
- AddQuickTunnel
- UseEphemeralKey
- CaptureUrlFromSession
- AddAzureRelayTunnel
- MapMcp
- Shiny.Net.HttpServer.Mcp
- Shiny.Net.HttpServer.Mediator
- MediatorHttpGroup
- MediatorHttpGet
- MediatorHttpPost
- MapGeneratedMediatorEndpoints
- MediatorDispatch
- Shiny.Net.HttpServer.DocumentDb
- MapDocuments
- MapDocumentCollection
- DocumentEndpoints
- DocumentResourceBuilder
- Shiny.Net.HttpServer.WebDav
- MapWebDav
- WebDavOptions
- WebDavMountBuilder
- WebDavMethods
- WebDavHeaderNames
- IWebDavPropertyStore
- InMemoryWebDavPropertyStore
- WebDavLock
- WebDavLockScope
- webdav
- RFC 4918
- PROPFIND
- PROPPATCH
- MKCOL
- LOCK
- UNLOCK
- mount a folder in Finder
- map a network drive
- Shiny.Net.HttpServer.Grpc
- MapGrpcService
- GrpcServiceBuilder
- GrpcOptions
- GrpcCallContext
- GrpcMarshaller
- GrpcMarshallerRegistry
- GrpcStatusException
- GrpcStatusCode
- GrpcMethodMetadata
- MapUnary
- MapServerStreaming
- MapClientStreaming
- MapDuplexStreaming
- grpc
- grpc-web
- protobuf
- proto file
- RPC service
- AppendTrailer
- DeclareTrailer
- Response.Trailers
- trailing headers
- SWS001
- SWS006
- SWM003
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
- Expose a folder as a WebDAV mount so it appears as a drive in Finder or Windows Explorer
- Read or write bodies in XML, MessagePack, protobuf or a format of their own instead of JSON
- Add authentication, authorization, CORS, rate limiting or IP filtering to that server
- Make a device-local server reachable from the internet through a tunnel
- Serve gRPC or gRPC-Web from an app that cannot run ASP.NET Core
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
dotnet add package Shiny.Net.HttpServer.Mediator         # Shiny.Mediator handlers as endpoints
dotnet add package Shiny.Net.HttpServer.DocumentDb       # Shiny.DocumentDb types as REST resources
dotnet add package Shiny.Net.HttpServer.WebDav           # a directory as a WebDAV mount (RFC 4918)
dotnet add package Shiny.Net.HttpServer.Grpc             # gRPC + gRPC-Web services
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

## Formats other than JSON

JSON is the default and is what to generate unless the user asks for something else. When they do,
**do not reach for `XmlSerializer`, `DataContractSerializer`, or MessagePack-CSharp's default
resolver** — all of them build their mapping by reflecting over the type and break a trimmed or AOT
build. Register a formatter instead. Formats plug into content negotiation, which lives at tier 1
(configuration) and applies to all four tiers above it:

```csharp
builder.Services.AddContentNegotiation(o =>
{
    o.NegotiateByDefault = true;   // Results.Ok(value) honours Accept; off by default
    o.AddXml();                    // application/xml, text/xml
    o.AddMessagePack();            // application/msgpack, application/x-msgpack
});
```

Both directions come from that one call, and **no endpoint changes**:

- **Responses** — `Results.Negotiate(value)` always negotiates; `Results.Ok(value)` and
  `new OkObjectResult(value)` negotiate only with `NegotiateByDefault = true`. `Results.Json(...)`
  never does, because the name states a format.
- **Request bodies** — chosen from `Content-Type`. Every `[FromBody]` parameter, mediator contract and
  `EndpointBinder.TryReadBodyAsync<T>` call accepts the registered formats immediately.

Both XML and MessagePack read the app's existing `JsonSerializerContext` metadata, so the DTOs need
**no format-specific attributes** — do not generate `[XmlRoot]`, `[DataMember]` or `[MessagePackObject]`.
A type is covered because it is in the `JsonTypeInfoRegistry`, same as for JSON.

Protobuf needs a schema, so its codecs are supplied, not discovered — one line per message type using
the pair `protoc` already generated:

```csharp
o.AddProtobuf(p => p
    .Add<Reading>(m => m.ToByteArray(), Reading.Parser.ParseFrom)
);
```

`BinaryCodecRegistry` is not protobuf-specific; `o.AddBinaryFormat("application/cbor", codecs)`
registers any binary encoding the same way. This is also the escape hatch for MessagePack-CSharp's
native codec when the built-in transcoding formatter's JSON type system is not enough.

Reading a body by hand, with the three outcomes kept distinct:

```csharp
var body = await EndpointBinder.TryReadBodyAsync<Probe>(ctx);

if (!body.Success)
{
    // NoBody/Malformed → 400, UnsupportedMediaType → 415
    await EndpointBinder.BodyReadFailedAsync(ctx, "probe", body.Status, nameof(Probe));
    return;
}
```

Gotchas to respect when generating:
- MessagePack maps must be **string-keyed**; integer keys are refused with a 400.
- A `byte[]` goes over MessagePack as base64, and a `decimal` beyond `double` precision loses digits —
  both because the shared metadata is JSON's.
- XML reading is type-directed, so `<postalCode>01234</postalCode>` into a `string` member stays
  `"01234"`. Do not add a converter to work around a problem that is not there.
- XML collections are a wrapper element containing child elements (`<tags><item>a</item></tags>`), not
  repeated siblings.
- A custom `IOutputFormatter` for a binary format should return `Charset => null`.

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

### WebDAV — a directory as a mountable drive

`MapFileBrowser` is a JSON API you drive with curl or your own client. `MapWebDav` speaks the
protocol the operating system already has a client for, so the folder mounts as a drive with no
client code at all. Reach for it whenever the user says *mount*, *Finder*, *Explorer*, *map a
network drive*, or *WebDAV*; reach for the file browser when they want an API.

Tier 1: a mounted module of raw routes, twenty-two of them, mapped in one call.

```csharp
app.MapWebDav("/dav", o =>
{
    o.RootPath = FileSystem.AppDataDirectory;
    o.AllowWrite = true;      // PUT, MKCOL, PROPPATCH, COPY, LOCK — off by default
    o.AllowDelete = true;     // DELETE, and the delete half of MOVE — off by default
})
.RequireAuthorization();      // or .RequireAuthorizationForChanges() to leave reads open
```

- **Always put authentication in front of a writable mount, and serve it over TLS.** WebDAV clients
  send Basic credentials on every request.
- Leave `EnableLocking` on. It is class 2, and Finder and the Windows redirector both mount a class 1
  server read-only however much `AllowWrite` allows.
- `AllowInfiniteDepth` is off by default: `PROPFIND` with `Depth: infinity` answers 403 with
  `<DAV:propfind-finite-depth/>`, which is the refusal RFC 4918 §9.1 defines. Turn it on only for a
  small tree.
- Dead properties (`PROPPATCH`) are held in memory. Assign `PropertyStore` to keep them across
  restarts.
- The mount is excluded from the OpenAPI document — do not try to describe it.

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
// Zero-account public HTTPS from a phone — pinggy by default, nothing to configure
builder.Services.AddQuickTunnel();
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
- `StartAsync` **can return null** — the tunnel connected but never learned an address. Check for it
  and show `LastError`; do not treat a non-throwing call as success. It takes a cancellation token,
  and a UI must leave the user a way to cancel rather than disabling every control behind one
  `IsBusy` flag while a network round trip is in flight.
- Host presets are not interchangeable, and only the default works with nothing provisioned:
  - `QuickTunnelHost.Pinggy` (**default**) — no account. Generates its own key via `UseEphemeralKey`.
    Anonymous tunnels expire after 60 minutes; pass an access token as the `subdomain` argument.
  - `QuickTunnelHost.Sish` — `tuns.sh` needs a key **enrolled at pico.sh**; an unknown key is
    refused. Use it when the address must stay stable, since it follows the key.
  - `QuickTunnelHost.LocalhostRun` — **cannot report its own address** through this library: it never
    confirms the session request carrying the URL, and SSH.NET waits for that confirmation where the
    `ssh` binary does not. Only usable with a known custom domain set as `PublicUrl`.
  - `QuickTunnelHost.Serveo` — frequently unreachable; do not suggest it for a demo.
- Writing `UrlPattern` for a provider of your own: anchor it to that provider's tunnel domain. Never
  "first `https://` in the output" — providers print a welcome banner full of links to their own site
  before announcing your address.
- Always put authentication in front of anything exposed by a tunnel.
- `AzureRelay` is deliberately **not** AOT-clean; do not suggest it for a trimmed/AOT app.
- There is no ngrok/Cloudflare provider by design — they need an agent process, impossible on
  iOS/Android.

## gRPC and gRPC-Web

Tier 1: a mounted module of raw routes — one `POST /{service}/{method}` per method. Reach for it when
the user says *gRPC*, *proto*, *RPC service* or *grpc-web*.

```csharp
app.MapGrpcService("greet.Greeter", svc =>
{
    // Marshalling is supplied, never discovered. Register once per message type, before the
    // methods that use it — a missing one throws at startup naming the method.
    svc.AddMarshaller<HelloRequest>(m => m.ToByteArray(), HelloRequest.Parser.ParseFrom);
    svc.AddMarshaller<HelloReply>(m => m.ToByteArray(), HelloReply.Parser.ParseFrom);

    svc.MapUnary<HelloRequest, HelloReply>("SayHello", (request, context) =>
        new ValueTask<HelloReply>(new HelloReply { Message = $"Hello {request.Name}" }));

    svc.MapServerStreaming<HelloRequest, HelloReply>("Greetings", Greetings);
    svc.MapClientStreaming<Reading, Summary>("Upload", async (requests, context) => …);
    svc.MapDuplexStreaming<Note, Note>("Chat", Chat);
})
.RequireAuthorization();
```

- **Do not** reference `Grpc.AspNetCore`, `Grpc.Core` or `Grpc.Core.Api` on the server side — none of
  them are used here. For `.proto` files add `Google.Protobuf` + `Grpc.Tools` with
  `GrpcServices="None"`: the generated *message* classes are what marshallers need, not the
  generated service base.
- Streams are `IAsyncEnumerable<T>` both ways. Generate `static async IAsyncEnumerable<TResponse>`
  local or member methods for streaming handlers rather than lambdas — a lambda cannot be an
  iterator.
- Failures the caller should see: `throw new GrpcStatusException(GrpcStatusCode.NotFound, "…")`.
  Anything else becomes `Unknown` with a generic message unless `EnableDetailedErrors` is set.
- Deadlines arrive as `context.CancellationToken` — thread it through every await. `grpc-timeout`
  expiry is reported as `DeadlineExceeded` automatically.
- Metadata: read `context.RequestHeaders`, write `context.ResponseTrailers` (writable all call long)
  and `context.ResponseHeaders` (until the first message goes out).
- Native gRPC needs HTTP/2. Over cleartext set `Options.Http2.AllowCleartext = true`; an HTTP/1.1
  caller gets a 505 pointing at gRPC-Web.
- gRPC-Web is on by default and is how a browser calls in. It has no client-streaming or duplex —
  those methods answer `Unimplemented` to a web caller. Pair it with a CORS policy that exposes
  `grpc-status` and `grpc-message`.
- The routes are excluded from OpenAPI; do not try to describe them.

## Trailing headers

`Response.Trailers` / `AppendTrailer` / `DeclareTrailer` send headers *after* the body, on HTTP/1.1,
HTTP/2 and HTTP/3. Use them to report what a handler could not know before it started writing — a
checksum, a row count, an outcome discovered mid-stream. This is what carries gRPC's status.

```csharp
ctx.Response.DeclareTrailer("X-Row-Count");
await WriteCsvAsync(ctx.Response.Body);
ctx.Response.AppendTrailer("X-Row-Count", rows.ToString());
```

On HTTP/1.1 the response **must not** declare a `Content-Length` — trailers ride the terminating
chunk, and there is no chunk on a length-delimited body. Do not set `ContentLength` on a handler
that appends trailers.

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

## Shiny.Mediator

`Shiny.Net.HttpServer.Mediator` publishes mediator handlers as endpoints. Same shape as
`Shiny.Mediator.AspNet`; the generator ships **inside** the package, so there is no second reference.

```csharp
[MediatorHttpGroup("/api/widgets", Tags = ["Widgets"], RequiresAuthorization = true)]
public class WidgetHandlers : IRequestHandler<GetWidget, Widget>, ICommandHandler<DeleteWidget>
{
    [MediatorHttpGet("/{id:int}")]
    public Task<Widget> Handle(GetWidget request, IMediatorContext ctx, CancellationToken ct) => …;

    [MediatorHttpDelete("/{id:int}")]      // ICommand -> 204, or SuccessStatusCode = 202
    public Task Handle(DeleteWidget command, IMediatorContext ctx, CancellationToken ct) => …;
}

app.MapGeneratedMediatorEndpoints();       // or Map{Handler}MediatorEndpoints()
```

- **Binding is decided by the verb, and it is not configurable.** `GET`/`DELETE` bind each contract
  member from a route token (matched by name) or the query string. `POST`/`PUT`/`PATCH` read the
  whole contract from the JSON body, then apply any route token over the top — so `PUT /widgets/{id}`
  works, and the URL wins over the body.
- A member bound from route/query must be `IParsable<T>`, an enum, a string, or an array of those —
  otherwise **SWM003** at build time. Nullable members and members with defaults are optional.
- A route token applied to a body-bound contract needs a record (`with`) or a settable property;
  init-only on a non-record is **SWM008**.
- `IStreamRequest<T>` becomes Server-Sent Events and **must be GET** (SWM007). `EventName` sets the
  SSE `event:` field.
- Contracts and results still need `[JsonSerializable]` — **SWM006** warns when a context misses one.
- Prefer the attributes. `MapMediatorPost<TRequest,TResult>(pattern)` etc. exist for runtime routes;
  the `GET`/`DELETE` overloads take a `bind` delegate because there is no reflection to fall back on.

**Two integration gotchas — mention both when generating this:**

1. `Shiny.Mediator`'s own generator turns a contract with `[Get]`/`[Post]`/`[Put]`/`[Patch]`/
   `[Delete]` into an HTTP *client*, and matches those attributes **by simple name with no namespace
   check** — so it errors (`SHINYMED_HTTP001`) on this server's minimal-endpoint attributes. A
   project using both needs `<NoWarn>$(NoWarn);SHINYMED_HTTP001</NoWarn>`.
2. Stream requests need an `IConfiguration` in the container (`AddShinyMediator` registers a
   timer-refresh stream middleware that takes one). A generic host has it; a hand-built
   `ServiceCollection` does not, and the failure lands *after* the SSE headers have gone out.

## Shiny.DocumentDb

`Shiny.Net.HttpServer.DocumentDb` publishes a document type as a REST resource. Same shape as
`Shiny.DocumentDb.AspNetCore`, on a server that runs in a MAUI app.

```csharp
app.MapDocuments<Order>("/orders", o =>
{
    o.Operations = DocumentEndpoints.All;     // default is Read | Count — the safe half
    o.TypeInfo = AppJson.Default.Order;       // REQUIRED here; there is no reflection fallback
    o.AllowFilterOn(x => x.Status, x => x.Total);
    o.Scope<ITenant>((t, ctx) => x => x.TenantId == t.Id);
})
.RequireAuthorization("orders");               // fans out to every route the resource mapped
```

- Maps `GET /`, `GET /{id}`, `GET /count`, `GET /stream` (SSE), `POST /`, `PUT /{id}`, `PATCH /{id}`,
  `DELETE /{id}` — whichever the `Operations` flags allow.
- **Always set `TypeInfo`.** Unlike the ASP.NET package there is no reflection fallback; without
  source-generated metadata the first request throws with a message naming the property.
- Query surface: `?filter=` (grammar), `?orderby=`, `?fields=` (sparse), `?skip=`/`?take=` (clamped to
  `MaxPageSize`, never refused), `?cursor=` (keyset; cannot be combined with `fields`).
- `Scope(...)` is the security boundary: AND-ed into reads, checked on **both sides** of a write,
  and an out-of-scope document is **404** not 403. Read the request from `ctx.Http` — the ambient
  `IHttpContextAccessor` is only published when `UseSessions()` is mapped.
- `MapDocumentCollection` is the schema-free lane (relational providers only). Scoped inserts and
  replaces are refused there by design — a raw JSON body cannot be checked against the scope.
- `PATCH` is RFC 7396: an explicit `null` **removes** the member.
- Mapping `DocumentEndpoints.Stream` on a provider without change monitoring is a startup error.

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
3. **Route constraints are a closed set** — `byte`, `short`, `int`, `long`, `float`, `double`,
   `decimal`, `bool`, `guid`, `alpha`, `datetime`, `dateonly`, `timeonly`, `timespan`,
   `minlength(n)`, `maxlength(n)`, `length(n)`, `min(n)`, `max(n)`, `range(a,b)`. There is **no
   `regex`** — validate anything richer in the handler so it can return a meaningful error instead of
   a 404. Length constraints count characters; `min`/`max`/`range` compare the value and may take
   negative arguments.
   A constraint only decides whether the route **matches** — it converts nothing, and the binder
   handles every `IParsable<T>` regardless. So `{id:int}` on a handler taking a `long` is fine, a
   refused segment is a **404**, and a matched-but-unparseable one is a **400**.
4. **Segments are literal or a parameter, never mixed** (`v{version}` is rejected at registration).
5. **Register middleware before starting**; register routes whenever you like.
6. **Put auth in front of static files** when they are not public.
7. **Pass `CancellationToken`** (or `ctx.RequestAborted`) into everything async.
8. **Never capture `HttpContext`** past the handler — contexts are pooled.
9. **Loopback by default** — only bind `IPAddress.Any` when the user asked for network access.
10. **A tunnel is the public internet.** Authentication and rate limiting first, always.

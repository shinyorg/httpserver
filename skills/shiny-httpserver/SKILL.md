---
name: shiny-httpserver
description: Generate code using Shiny.Net.HttpServer — a dependency-light, AOT/trim-clean HTTP/1.1, HTTP/2 & HTTP/3 server that runs anywhere .NET runs, including .NET MAUI and native tvOS, where ASP.NET Core cannot. Covers routing, middleware, source-generated typed endpoints, results and JSON, content negotiation with XML/MessagePack/protobuf formatters in both directions, static files and Blazor WASM, uploads/downloads, WebSockets, SSE, SignalR-style switchboards (real-time two-way calls over SSE with groups, an operator, client results, resumable lines, and a generated typed .NET client), sessions, OpenAPI, authentication (Basic/API key/cookie/JWT), authorization, CORS, rate limiting, IP filtering, TLS and self-signed certificates, tunnelling (relay, SSH, quick tunnels, Azure Relay, and supervised cloudflared/ngrok/tailscale agents), serving a directory over WebDAV, serving gRPC and gRPC-Web, hosting an MCP server with RFC 9728 OAuth discovery, receiving the OAuth/OIDC loopback redirect so a desktop app or CLI can sign in through the system browser (RFC 8252), health checks, OpenTelemetry-shaped metrics and tracing, W3C access logs, request timeouts, output caching and conditional requests, request decompression, antiforgery and browser security headers, a reverse proxy with destination clusters, load balancing, health checks, session affinity, transforms, WebSocket forwarding and IConfiguration-driven routes, mDNS/Bonjour advertising and discovery, MAUI lifecycle (background/foreground, Android foreground service, network rebinding), host filtering against DNS rebinding, request localization, webhook signature verification (GitHub/Stripe/Slack/Standard Webhooks), idempotency keys, RFC 9530 content digests, 103 Early Hints, the PROXY protocol, RFC 6902 JSON Patch, API versioning with per-version OpenAPI, a Scalar API reference page, tus resumable uploads, CalDAV/CardDAV, automatic HTTPS via ACME (Let's Encrypt/ZeroSSL), and an in-memory test harness.
auto_invoke: true
triggers:
- Shiny.Net.HttpServer
- HttpServer
- embedded http server
- http server in MAUI
- http server on tvOS
- tvos
- Apple TV
- web server on device
- HttpServerOptions
- ShinyHttpServerBuilder
- AddShinyHttpServer
- AddHttpServer
- MapGet
- MapPost
- OnRequest
- IHttpMiddleware
- IResponseBodyControl
- request logging
- traffic recorder
- capture request body
- capture response body
- read body twice
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
- ZipFileSource
- serve a zip
- serve static files from a zip
- MapFileBrowser
- FileDownloadResult
- ReadMultipartAsync
- AcceptWebSocketAsync
- AcceptTrackedWebSocketAsync
- IWebSocketRegistry
- WebSocketRegistry
- permessage-deflate
- AddHealthChecks
- MapHealthChecks
- IHealthCheck
- HealthCheckResult
- UseTelemetry
- UseW3CLogging
- AddW3CLogging
- W3CLoggerOptions
- W3CLoggingFields
- IW3CLogWriter
- access log
- w3c log
- HttpServerTelemetry
- HttpServerMetrics
- traceparent
- OpenTelemetry
- UseRequestDecompression
- UseRequestTimeouts
- RequestTimeout
- DisableRequestTimeout
- UseOutputCache
- CacheOutput
- OutputCache
- IOutputCacheStore
- CheckPreconditions
- TryCompleteConditionalAsync
- SetETag
- EntityTag
- UseSecurityHeaders
- UseHttpsRedirection
- Hsts
- AddAntiforgery
- UseAntiforgery
- IAntiforgery
- ValidateAntiforgery
- MapProxy
- ProxyOptions
- MapReverseProxy
- AddReverseProxy
- reverse proxy
- YARP
- ProxyCluster
- ProxyDestination
- LoadBalancingPolicy
- ILoadBalancingPolicy
- SessionAffinityMode
- TransformBuilder
- HttpForwarder
- ReverseProxyRuntime
- ReverseProxyConfiguration
- ForwardUpgrades
- load balancing
- health checks for destinations
- Shiny.Net.HttpServer.Proxy
- AddHttpServerAdvertisement
- IHttpServerAdvertiser
- AddHttpServerLocator
- IHttpServerLocator
- mDNS
- Bonjour
- DNS-SD
- service discovery
- AddHttpServerLifecycle
- BackgroundServerMode
- HttpServerForegroundService
- LocalNetworkAccess
- RebindOnNetworkChange
- NetworkAddressesChanged
- LocalAddresses
- StateChanged
- StateTransitioned
- HttpServerStateChange
- HttpServerStateReason
- LastStateChange
- HttpServerListenerException
- RecoverFromListenerFaults
- StartRetryAttempts
- AcceptRetryAttempts
- RestartAttempts
- RestartRetryDelay
- MaxRestartRetryDelay
- PublishAttempts
- PublishRetryDelay
- MaxPublishRetryDelay
- TestHttpServer
- CreateInMemoryClient
- InMemoryConnectionHandler
- ITunnelAgent
- CloudflareTunnel
- NgrokTunnel
- TailscaleFunnel
- AddMcpProtectedResource
- MapMcpProtectedResource
- SendEventsAsync
- ServerSentEvents
- Switchboard
- Shiny.Net.HttpServer.Switchboard
- Shiny.Net.HttpServer.Switchboard.Client
- AddSwitchboard
- MapSwitchboard
- IOperator
- CallerContext
- LineMethod
- NotALineMethod
- ISwitchboardFilter
- AddSwitchboardFilter
- ExportContract
- SwitchboardContractsOutputPath
- SwitchboardLine
- SwitchboardLineBuilder
- SwitchboardClient
- HangUpAsync
- SignalR
- hub
- IHubContext
- real-time push to clients
- UseSessions
- ISession
- MapOpenApi
- AddAuthentication
- AddAuthorization
- OptionsRegistration
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
- RelayTunnel
- TunnelConnected
- TunnelDisconnected
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
- Shiny.Net.HttpServer.CommandLine
- shinyhttpserver
- serve a directory from the command line
- file manager in a browser
- mount a folder as a drive
- QR code in the terminal
- open a served directory on a phone
- share a folder over the internet
- --tunnel
- --tunnel-token
- --no-tui
- shinyhttpserver dashboard
- watch requests in the terminal
- see uploads and downloads in progress
- Shiny.Net.HttpServer.WebDav
- MapWebDav
- WebDavOptions
- WebDavMountBuilder
- WebDavMethods
- WebDavHeaderNames
- IWebDavPropertyStore
- IWebDavFileSystem
- PhysicalWebDavFileSystem
- WebDavEntry
- WebDavException
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
- Shiny.Net.HttpServer.OAuthLoopback
- LoopbackCallbackListener
- LoopbackCallbackOptions
- LoopbackCallbackResult
- SystemBrowser
- AuthorizeAsync
- OAuth loopback
- loopback redirect
- redirect uri localhost
- RFC 8252
- sign in with browser
- desktop OAuth login
- CLI login
- HttpListener OAuth
- IBrowser OidcClient
- SWS001
- SWS006
- SWM003
- SendEarlyHintsAsync
- SendInformationalResponseAsync
- Early Hints
- 103 Early Hints
- Status103EarlyHints
- rel=preload
- 1xx informational response
- AddHostFiltering
- UseHostFiltering
- HostFilteringOptions
- AllowedHosts
- DNS rebinding
- Host header
- AddRequestLocalization
- UseRequestLocalization
- RequestLocalizationOptions
- RequestCulture
- GetRequestCulture
- Accept-Language
- CookieRequestCultureProvider
- localization
- InvariantGlobalization
- MapWebhook
- WebhookSignature
- WebhookContext
- RequireWebhookSignature
- UseWebhookVerification
- IWebhookVerifier
- WebhookSigner
- webhook signature
- Stripe webhook
- GitHub webhook
- Standard Webhooks
- ProxyProtocolOptions
- ProxyProtocolMode
- UseProxyProtocol
- ProxyProtocolInfo
- PROXY protocol
- proxy protocol v2
- HAProxy
- send-proxy
- AWS NLB
- load balancer client IP
- JsonPatchDocument
- JSON Patch
- json-patch+json
- RFC 6902
- JsonPointer
- JSON Pointer
- JsonPatchException
- Accept-Patch
- Shiny.Net.HttpServer.Tus
- MapTus
- TusOptions
- TusMountBuilder
- ITusStore
- DiskTusStore
- TusMetadata
- TusUpload
- TusException
- WithTusHeaders
- tus
- resumable upload
- resume upload
- tus-js-client
- Uppy
- TusDotNetClient
- Idempotency-Key
- AddIdempotency
- UseIdempotency
- RequireIdempotencyKey
- IdempotentAttribute
- IIdempotencyStore
- Content-Digest
- Repr-Digest
- RFC 9530
- AddContentDigest
- UseContentDigest
- ContentDigestAttribute
- DigestFields
- ApiVersion
- AddApiVersioning
- ApiVersioningOptions
- ApiVersionReader
- MapToApiVersion
- ApiVersionNeutral
- HasApiVersion
- NewApiVersionSet
- apiVersion constraint
- api versioning
- api-supported-versions
- Sunset header
- Deprecation header
- SWS030
- Shiny.Net.HttpServer.CalDav
- MapCalDav
- MapCalDavWellKnown
- CalDavOptions
- CalDavMountBuilder
- CalDavMethods
- ICalendarStore
- IAddressBookStore
- IDavCollectionStore
- FileCalendarStore
- FileAddressBookStore
- CalendarCollection
- AddressBookCollection
- DavObject
- ContentComponent
- caldav
- carddav
- icalendar
- vcard
- sync calendar with iPhone
- subscribe to calendar
- Shiny.Net.HttpServer.Acme
- AddAcme
- ACME
- Let's Encrypt
- LetsEncrypt
- ZeroSSL
- AcmeCertificateManager
- AcmeOptions
- AcmeCertificateRegistry
- AcmeRegistryOptions
- AddAcmeRegistry
- AcmeRegistryEntry
- AcmeCertificateState
- SNI certificate selection
- certificate per host
- import certificate
- edge server
- AcmeDirectories
- automatic HTTPS
- certificate renewal
- CertificateContextSelector
- ChallengeResponder
- TLS-ALPN-01
- HTTP-01
- MapScalarApiReference
- Scalar
- ScalarOptions
- ScalarTheme
- API reference page
- Swagger UI
- API explorer
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
- Host a Model Context Protocol server inside a non-ASP.NET app, including one a remote client must
  authenticate to
- Find or be found by another device on the same network without anyone typing an IP address
- Keep an embedded server working as a phone is backgrounded, resumed, or moved between networks
- Push to clients and let them call back, SignalR-style (a switchboard), including from a MAUI app,
  with a .NET client that resumes after a drop
- Report health, metrics or traces from an embedded server
- Cache responses, honour conditional requests, bound how long a handler may take, or forward a
  route to another server
- Test endpoints without binding a port
- Sign a desktop app or CLI in through the system browser (the OAuth/OIDC loopback redirect,
  what VS Code and `gh auth login` do) without `HttpListener`

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
dotnet add package Shiny.Net.HttpServer                  # the server + the typed-endpoint generator
dotnet add package Shiny.Net.HttpServer.Jwt              # JWT auth
dotnet add package Shiny.Net.HttpServer.Proxy            # reverse proxy: clusters, LB, health, transforms
dotnet add package Shiny.Net.HttpServer.Ssh              # SSH + quick tunnels
dotnet add package Shiny.Net.HttpServer.AzureRelay       # Azure Relay tunnel (NOT AOT-clean)
dotnet add package Shiny.Net.HttpServer.Mcp              # Model Context Protocol transport
dotnet add package Shiny.Net.HttpServer.Mediator         # Shiny.Mediator handlers as endpoints
dotnet add package Shiny.Net.HttpServer.DocumentDb       # Shiny.DocumentDb types as REST resources
dotnet add package Shiny.Net.HttpServer.WebDav           # a directory as a WebDAV mount (RFC 4918)
dotnet add package Shiny.Net.HttpServer.Grpc             # gRPC + gRPC-Web services
dotnet add package Shiny.Net.HttpServer.Discovery        # mDNS/Bonjour advertise + browse
dotnet add package Shiny.Net.HttpServer.Mobile           # mobile lifecycle (multi-targets android/ios/maccatalyst; not tvOS)
dotnet add package Shiny.Net.HttpServer.Testing          # in-memory HttpClient for tests
dotnet add package Shiny.Net.HttpServer.Tunnels          # cloudflared / ngrok / tailscale agents (desktop + CLI only)
dotnet add package Shiny.Net.HttpServer.OAuthLoopback    # OAuth/OIDC loopback redirect receiver (desktop + CLI only)
dotnet add package Shiny.Net.HttpServer.Tus              # tus 1.0.0 resumable uploads
dotnet add package Shiny.Net.HttpServer.CalDav           # calendars & contacts over CalDAV/CardDAV
dotnet add package Shiny.Net.HttpServer.Acme             # automatic HTTPS via Let's Encrypt/ZeroSSL (public hosts, not phones)

dotnet tool install -g Shiny.Net.HttpServer.CommandLine  # `shinyhttpserver`, not a library reference
```

`Shiny.Net.HttpServer.CommandLine` is a .NET tool, not something an app references: it serves a
directory over HTTP from a terminal (`shinyhttpserver [path] -m read|create|update|delete|all
-u user:password`). Reach for it when the ask is "serve this folder", not "add a server to my app".
It mounts the directory over **WebDAV**, so one address is both a browser file manager (browse,
upload, rename, delete — whatever `-m` allows) and a drive Finder, Explorer or a Linux file manager
can mount. Scripting it is WebDAV: `GET`/`PUT`/`DELETE` as usual, `MKCOL` for a directory, `MOVE`
for a rename, and `PROPFIND` with `Depth: 1` for a machine-readable listing — a `GET` on a directory
returns the manager's HTML, not JSON. `-m` is enforced before the handler runs, across `MKCOL`,
`COPY` and `MOVE` as well as `PUT`; a `MOVE` is judged by where it lands, and renaming needs
`update` **and** `delete`.
It listens on every interface by default and ends its banner with a scannable QR code of the LAN
address plus the URL in full, so "get this folder onto my phone" is the tool answer, not code —
`-a localhost` keeps it to the machine, `--no-qr` drops the code. Basic auth (`-u`) over plain HTTP
refuses to start on a non-loopback address, which the default now is: pair it with `--https`.

`--tunnel` is the answer to "share this folder with someone not on my network": it opens a
`QuickTunnel` to pinggy.io and the QR code carries the public HTTPS address instead of the LAN one.
Because the tunnel feeds `HttpServer.ServeAsync` directly, `--tunnel -a localhost` binds nothing on
the LAN and is reachable only through the tunnel — and a tunnelled connection counts as encrypted
transport, so `-u` works over it without `--https` or `--allow-insecure-auth`. Anonymous tunnels stop
after 60 minutes; `--tunnel-token <token>` lifts that and implies `--tunnel`. Always say that the
address is public when you suggest it.

In a terminal, `shinyhttpserver` opens a full-screen **dashboard** rather than printing the banner:
Overview (addresses, tunnel, warnings, QR code), Requests (every request, newest first, including
`401`s and tunneled clients; `Enter` for headers, `Ctrl+L` clears), Transfers (uploads and downloads
in flight with progress, speed and time left), Settings (every flag as a form, applied live with
`Ctrl+S`), and Log. So "watch who is hitting this folder" or "see how far the upload got" is the tool
answer too. Settings changes to the tunnel, its token, the QR code or the log level apply in place;
anything else rebuilds the server, and the dashboard asks first when that would cut off a transfer
and rolls back if the new settings will not start. `--no-tui` keeps the plain banner, and a
redirected run (a script, a pipe, a service) gets the banner whatever the flags say, so suggest
`--no-tui` only when a user wants plain output in an interactive terminal. The dashboard is part of
the tool, not a library API, so it sits outside the four tiers below. An app that wants the same
request log writes a middleware (tier 3) that wraps `Request.Body` and `Response.BodyControl`, the
way the tool does.

## The four tiers — the spine of this library

Every new API belongs to one of these. Say which when you introduce one. They compose in one app.

| Tier | What it is | Use when |
| --- | --- | --- |
| 0 | `OnRequest(ctx => …)` — one delegate, no routing | A single handler, a test fixture, a fallback |
| 1 | `MapGet`/`MapPost`/… — raw handlers behind a route template | A handful of routes, no binding wanted |
| 2 | `Use(...)` / `IHttpMiddleware` — the pipeline | Cross-cutting work |
| 3 | `[Route]` classes + the source generator | Anything real: typed parameters, DI, OpenAPI |

**Default to tier 3** when the user has more than a couple of endpoints or wants typed parameters.
Default to tier 1 for small, script-like servers. Never suggest reflection-based alternatives.

## Setup

Choose the host shape from who owns the container:

```csharp
// (a) No container — smallest possible
var server = new HttpServer(new HttpServerOptions { Port = 8080 });
server.MapGet("/ping", ctx => ctx.Response.WriteAsync("pong"));
await server.RunAsync();

// (b) The builder — a console app or service that wants DI
var builder = HttpServer.CreateBuilder();
builder.Options.Port = 8080;
builder.AddAuthentication().AddJwtBearer(o => { o.Issuer = "app"; o.Audience = "app"; o.SigningKey = key; });   // server features: on the builder
builder.Services.AddSingleton<IWidgetStore, WidgetStore>();          // your own: on Services
var app = builder.Build();
app.MapMyAppEndpoints();
await app.RunAsync();

// (c) An existing container — MAUI, generic host. Same builder, same calls.
services.AddShinyHttpServer(
    http =>
    {
        http.Options.Address = IPAddress.Any;
        http.Options.Port = 0;

        http.AddAuthentication().AddBasic<CredentialStore>(o => o.Realm = "Device");
        http.Configure(server => server.MapMyAppEndpoints());
    },
    autoStart: false     // for an app with a "share" toggle
);
```

**Every registration in this library is an extension on `ShinyHttpServerBuilder`, not on
`IServiceCollection`** — `AddAuthentication`, `AddCors`, `AddRateLimiter`, `AddHealthChecks`,
`AddOutputCache`, `AddSessions`, the tunnels, discovery, the mobile lifecycle. Generate
`builder.AddX(...)`, never `builder.Services.AddX(...)`, for anything this library owns;
`builder.Services` is for the app's own registrations. The one exception is
`services.AddHttpServerLocator()`, which is the client half of mDNS and has no server to hang off.

`http.Configure(server => ...)` is where routes and middleware go in shape (c) — it runs when the
server is first resolved, so it can pull things out of the container.

`autoStart: false` + `server.StartAsync()` from the UI is the right shape for MAUI. `Port = 0` lets
the OS pick; read it back from `server.ListenUrl`.

### Defaults worth knowing

- Binds **loopback** by default. Set `Address = IPAddress.Any` for LAN access — deliberately.
- `Limits.MaxRequestBodySize` is 30 MB; raise it for uploads.
- `HideExceptionDetails` is on; turn it off in development only.

### Behind a TCP load balancer: PROXY protocol

Listener configuration, underneath all four tiers — handler code only reads `ctx.Connection`.

```csharp
builder.Options.Listen(IPAddress.Any, 8080)
    .UseProxyProtocol(p => p.Trust("10.0.0.0/8"));   // the BALANCER's addresses, not clients'
// single endpoint: builder.Options.ProxyProtocol = new ProxyProtocolOptions { Mode = ProxyProtocolMode.Required }.Trust("10.0.0.0/8");
```

- v1 and v2 are both accepted. The header is read before TLS, so TLS passthrough works. `ctx.Connection.RemoteIpAddress`/`RemotePort` become the client's, and the IP filter, rate limiter and W3C logs need nothing else.
- `TrustedProxies` must not be empty, or the server refuses to start. Only generate `.Trust("0.0.0.0/0").Trust("::/0")` when the user says the port is firewalled so only the balancer can reach it.
- `Required` (the default for `UseProxyProtocol`) closes connections that have no header. Use `Optional` only while migrating.
- `ctx.Connection.ProxyProtocol` (null if none): `ProxyEndPoint`, `DestinationEndPoint`, `Alpn`, `Authority`, `UniqueId`, `Ssl`, `GetTlv(0xEA)`.
- Prefer this over `UseForwardedHeaders`: it can't be spoofed by clients and it works with TLS passthrough. `UseForwardedHeaders` trusts **any** sender (no trusted-proxy list) and applies to HTTP/1.1 only — never generate it for a server that faces the internet directly. Don't enable both unless the balancer overwrites `X-Forwarded-For`, because that header overrides the PROXY address on HTTP/1.1.
- TCP endpoints only: not HTTP/3, not tunnel providers. The reverse proxy does not send a PROXY header.

## Lifecycle — and knowing *why* the server stopped

Start and stop are runtime operations, not just process startup and shutdown. An app with a toggle
flips this switch repeatedly, so the transitions are serialized, idempotent and restartable.

```csharp
await server.StartAsync();     // binds and begins accepting
await server.StopAsync();      // unbinds, then drains in-flight requests
await server.RestartAsync();   // both as one operation, re-reading Options
```

`StateChanged` gives a UI its transitions. **`StateTransitioned` is what to generate whenever the app
needs to diagnose a server that went down** — it carries an `HttpServerStateChange` with the reason
and the exception:

```csharp
server.StateTransitioned += (_, change) =>
{
    // Restarting is the stop half of a rebind — a start is coming, so leave the UI alone.
    if (change is { State: HttpServerState.Stopped, Reason: not HttpServerStateReason.Requested and not HttpServerStateReason.Restarting })
        logger.LogError(change.Exception, "The server went down: {Reason}", change.Reason);
};
```

| `HttpServerStateReason` | Meaning |
| --- | --- |
| `Requested` | The app called `StartAsync`/`StopAsync`. The only reason that is never a fault |
| `Restarting` | Part of a `RestartAsync` — **including the stop half**, so a subscriber knows a start is coming |
| `NetworkChanged` | A rebind driven by the addresses changing |
| `BindFailed` | The bind was refused and the retries are spent; `Exception` says why |
| `ListenerFaulted` | The listener stopped accepting while the server believed it was running |
| `Disposed` | The server was disposed |

`server.LastStateChange` is the same record, for code that was not subscribed at the time — a crash
report, a diagnostics screen, a background task that woke up to find the server down.

**Resilience is on by default; do not generate an opt-in for it.** A transient accept failure is
retried with backoff; a listener that dies underneath a running server is logged at error, reported
as `ListenerFaulted`, and rebound; the start half of a restart or a network rebind retries
(`StartRetryAttempts`, default 5). A plain `StartAsync` is deliberately **not** retried — its caller
gets the exception. Tune with `RecoverFromListenerFaults`, `StartRetry*` and `AcceptRetry*` only when
asked.

Handlers on `StateChanged`, `StateTransitioned` and `NetworkAddressesChanged` are isolated: one that
throws is logged and the rest still run. Do not wrap them in defensive try/catch of your own.

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

### JSON Patch (RFC 6902)

Tier 3 (parameter binding); `ReadJsonPatchAsync` serves tier 1/2 raw handlers.
A `JsonPatchDocument` parameter binds from an `application/json-patch+json` body. Anything else is a
415 with `Accept-Patch`, and a malformed patch is a 400. No `[JsonSerializable]` entry is needed for it.
Apply it with the target type's `JsonTypeInfo`, never by reflection:

```csharp
[Patch("/{id:int}")]
public IResult Patch(int id, JsonPatchDocument patch)
{
    if (notes.Find(id) is not { } note) return Results.NotFound();
    try
    {
        var patched = patch.ApplyTo(note, AppJson.Default.Note);   // atomic: works on a copy
        notes.Save(patched);
        return Results.Ok(patched, AppJson.Default.Note);
    }
    catch (JsonPatchException ex) { return ex.ToResult(); }     // 409 test/path, 422 bad result
}
```

- Raw handler: `var patch = await ctx.Request.ReadJsonPatchAsync(ctx.RequestAborted);` (throws `JsonPatchException`).
- Paths are JSON Pointers using the *serialized* names (`/title`, not `/Title`); `/-` appends to an array.
- Build one in code: `new JsonPatchDocument().Test("/version", 3).Replace("/title", "x").Add("/tags/-", "y")`.
- Do not send a JSON Patch as `application/json`: the server answers 415.

### API versioning (tier 1 + tier 3 — routing, not middleware)

```csharp
builder.AddApiVersioning(o =>                    // optional; defaults: query "api-version" + URL segment
{
    o.AssumeDefaultVersionWhenUnspecified = true; // else no version = 400
    o.ReportApiVersions = true;                   // api-supported-versions / api-deprecated-versions
    o.ApiVersionReader = ApiVersionReader.Combine(new UrlSegmentApiVersionReader(), new HeaderApiVersionReader("api-version"));
    o.Policies.Sunset(1.0).Effective(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)).Link("https://example.com/retire");
});

[Route("/api/{version:apiVersion}/users")]      // NOT "v{version:apiVersion}" — the constraint accepts "v2"
[ApiVersion("1.0")] [ApiVersion("2.0")] [ApiVersion("0.9", Deprecated = true)]
public class UserEndpoints
{
    [Get] public UserV1[] List() => …;                           // implicit: 0.9, 1.0
    [Get] [MapToApiVersion("2.0")] public UserV2[] ListV2() => …; // explicit mapping wins for 2.0
}

app.MapGet("/weather", V1).HasApiVersion(1.0);                  // raw: applies to the last-mapped route
app.MapGet("/weather", V2).HasApiVersion(2.0);                  // same template coexists by version
app.MapGroup("/api", api => api.HasApiVersion(2.0).MapGet("/x", X));
app.MapOpenApi("/openapi/{documentName}.json");                 // /openapi/v1.json, /openapi/v2.json
```
- No `UseApiVersioning()` with DI: selection runs inside routing. Without a container, use `app.UseApiVersioning(o => …)`.
- Endpoints without versions are untouched. `[ApiVersionNeutral]` accepts any version or none.
- Errors are 400 problem details (`UnsupportedApiVersion`, `AmbiguousApiVersion`, `InvalidApiVersion`, `ApiVersionUnspecified`). The handler reads its version with `ctx.GetRequestedApiVersion()`.
- Versions of one route must share one template.

### API reference page (Scalar)

Tier 1 — one route, mapped next to `MapOpenApi`. Generate it whenever the user asks for Swagger UI,
an API explorer, API docs page, or Scalar.

```csharp
app.MapOpenApi();
app.MapScalarApiReference();                                   // → /scalar

// per-version documents: Scalar shows a version picker, newest selected
app.MapOpenApi("/openapi/{documentName}.json");
app.MapScalarApiReference(configure: o =>
{
    o.DocumentUrl = "/openapi/{documentName}.json";
    o.DefaultHttpClient = ("csharp", "httpclient");
    o.Theme = ScalarTheme.Purple;
});
```

- Nothing is bundled: the page loads Scalar from the CDN **in the browser**. For an offline LAN (phone + laptop, no internet), serve `dist/browser/standalone.js` as a static file and set `o.ScriptUrl = "/scalar.js"`.
- `DocumentUrl` must match the `MapOpenApi` pattern; it is written into the page as-is (root-relative by default).
- Options use Scalar's names; unset = Scalar's default. Unnamed keys go in `o.AdditionalConfiguration["key"] = JsonNode`. Never put secrets there.
- `.RequireAuthorization()` right after the call protects the page — protect the document route too.
- A self-only CSP (`SelfOnlyContentSecurityPolicy`) blocks the CDN and inline script.
- There is no Swagger UI helper; use Scalar.

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

Generated `[Route]` classes mount into a group too — the generator emits an `IEndpointRouteBuilder`
overload beside the `HttpServer` one, and `[Authorize]`/`[AllowAnonymous]` travel with the routes:

```csharp
app.MapGroup("/v2", api => api.MapWidgetEndpoints());   // or api.MapMyAppEndpoints()
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
builder.AddContentNegotiation(o =>
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
app.UseHostFiltering();         // first: a request to the wrong name learns nothing
app.UseTelemetry();             // outermost: everything below it is time the client waited
app.UseW3CLogging();            // same reason — the line should describe the whole exchange
app.UseCors();                  // preflights carry no credentials
app.UseRateLimiter();           // before routing: a throttled request should cost nothing
app.UseIpFilter();
app.UseSecurityHeaders();       // applied as the response starts, so it covers static files and errors
app.UseContentDigest();         // before (de)compression: the digest covers the bytes on the wire
app.UseResponseCompression();
app.UseRequestDecompression();  // before anything that reads a body
app.UseWebhookVerification();   // after routing; only for [RequireWebhookSignature]
app.UseAuthentication();        // before routing
app.UseRequestLocalization();   // after auth (a provider may read a claim), before anything that renders text
app.UseAuthorization();         // after routing (registers itself as after-routing)
app.UseIdempotency();           // after authorization: the caller is part of the key
app.UseAntiforgery();           // after authentication — checks a request that would have succeeded
app.UseRequestTimeouts();       // after routing: the timeout is a property of the endpoint
app.UseOutputCache();           // after routing, so a hit still pays for auth above it
app.UseSessions();
app.UseStaticFiles("./wwwroot");
```

`UseSecurityHeaders`, `UseRequestDecompression`, `UseTelemetry` and `UseRateLimiter` are ordinary
middleware; `UseAuthorization`, `UseAntiforgery`, `UseRequestTimeouts` and `UseOutputCache` register
themselves **after routing**, because each reads endpoint metadata.

### Seeing the bodies (request logging, traffic recording, audit)

Both directions have a seam; use them rather than trying to read a body twice off the wire.

```csharp
// inbound: read once, hand the handler a rewound copy (also drops any BodyReader handed out)
var buffered = new MemoryStream();
await ctx.Request.Body.CopyToAsync(buffered, ctx.RequestAborted);
buffered.Position = 0;
ctx.Request.Body = buffered;

// outbound: wrap the control the response is bound to, then bind the wrapper
var tee = new TeeBodyControl(ctx.Response.BodyControl, capture);   // : IResponseBodyControl
ctx.Response.Bind(tee);
try     { await next(ctx); }
finally { await tee.FlushAsync(); }        // see gotcha below
```

A wrapper must forward `StartAsync`/`CompleteAsync` to the control it wrapped, and should build its
`Writer` over its own `Stream` (not over `inner.Writer`) so both write paths meet in one place. This
is the same seam `UseResponseCompression()` inserts itself through. On a device, decide from the
content type whether a body is worth keeping at all and cap what you store.

**Critical gotchas:**

- **Flush what a body-control wrapper buffered before the pipeline unwinds.** The connection
  completes its own producer, not whatever the response ended up bound to, so bytes left in a
  wrapper's `PipeWriter` never reach the wire.
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
builder.AddAuthentication()
    .AddJwtBearer(o => { o.Issuer = "app"; o.Audience = "app"; o.SigningKey = key; });
    // or .AddBasic(o => o.AddUser("ada", pw, "admin")) / .AddBasic<UserStore>()
    // or .AddApiKey(o => o.AddKey(key, "ci", "deploy"))
    // or .AddCookie(o => { o.Protector = new TicketProtector(k); o.LoginPath = "/login"; })

builder.AddAuthorization(o =>
{
    o.AddPolicy("admin", p => p.RequireRole("admin"));
    // o.SetFallbackPolicy(p => p.RequireAuthenticatedUser());   // deny by default
});
```

- `[Authorize]` / `[AllowAnonymous]` on endpoint classes and methods; `RequireAuthorization(...)` /
  `AllowAnonymous()` on raw routes (they apply to the **most recently mapped** route). A raw route may
  instead take the attribute instances as metadata — `app.Map("GET", "/x", h, new AuthorizeAttribute("admin"))`
  — with the same effect.
- **Every `Add…` registration composes.** `AddAuthorization`, `AddCors`, `AddRateLimiter` and the rest can
  be called more than once; each call's configuration applies, in order. When writing an `Add…` for a
  package built on this library, register options with `OptionsRegistration.Configure(builder.Services,
  configure)` — **never** `TryAddSingleton(_ => { var o = new T(); configure(o); return o; })`, which keeps
  the first call and silently drops the rest.
- A method's `[Authorize]` **adds to** the class's; `[AllowAnonymous]` always wins.
- 401 = anonymous, 403 = authenticated but not permitted. Never put the denial reason in the body.
- JWT needs a signing key, `Issuer` **and** `Audience` (or validation of either turned off deliberately) —
  otherwise the options throw the first time they are built. Generate all three.
- JWT: `JwtSigningKey.FromSecret/FromRsa/FromEcdsa`; `AddJwtBearer` also registers a
  `JwtTokenGenerator` — inject it in a login endpoint so issuing and validating cannot drift.
  Never generate a key at startup in production code (it invalidates every issued token on restart).
- Basic auth **refuses plain HTTP** off loopback. That is intentional; do not work around it.

CORS / rate limiting / IP filtering all follow the same shape — inline policy, or named policies plus
`RequireX("name")` / `DisableX()` on routes and `[EnableCors]`, `[EnableRateLimiting]`,
`[RequireIpFilter]` (+ `[DisableCors]`, `[DisableRateLimiting]`, `[AllowAnyIp]`) on endpoints.

### Host filtering (DNS rebinding) and localization

Tier 2 (middleware).

```csharp
builder.AddHostFiltering(o => o.AllowedHosts.Add("*.local"));   // add names; loopback, IP literals, tunnels already pass
app.UseHostFiltering();                                          // FIRST in the pipeline

builder.AddRequestLocalization(o => o.SetDefaultCulture("en")
    .AddSupportedCultures("en", "fr", "de").AddSupportedUICultures("en", "fr", "de"));
app.UseRequestLocalization();          // or just app.UseRequestLocalization("en", "fr", "de")
var culture = ctx.GetRequestCulture(); // CultureInfo.Current(UI)Culture are already set for the request
```

- Generate `UseHostFiltering()` for any server bound to `0.0.0.0` / `IPAddress.Any` that a browser can reach.
- An **empty `AllowedHosts` is not allow-all** (unlike ASP.NET); `"*"` is. Do not add IP addresses or `localhost`, they already pass.
- A browser visiting `myphone.local` needs `*.local` listed. Tunnel **agents** (cloudflared/ngrok/tailscale) need `o.AllowPublicUrl(() => agent.PublicUrl)`; `ITunnelProvider` connections pass by default.
- Refusal is a 400 with problem details. With `UseForwardedHeaders`, both `Host` and `X-Forwarded-Host` must pass.
- Localization: providers are query (`culture`/`ui-culture`), then cookie (`CookieRequestCultureProvider.DefaultCookieName`, write it with `MakeCookieValue`), then `Accept-Language`. `AddSupportedCultures` **replaces** the list.
- Under `InvariantGlobalization` (AOT/mobile) every request is invariant and nothing throws; tell the user to set `<PredefinedCulturesOnly>false</PredefinedCulturesOnly>` if they localize text with `.resx`.

## Webhooks — receiving (and signing) signed deliveries

Tier 1 (routing) for `MapWebhook`, the default. The metadata form is tier 2 (after-routing middleware) plus tier 3 (attribute and a `WebhookContext` parameter).

```csharp
using Shiny.Net.HttpServer.Webhooks;

// Default: verification is part of the route. No registration, no middleware.
app.MapWebhook("/hooks/github", WebhookSignature.GitHub(secret), async (WebhookContext ctx) =>
{
    if (ctx.EventType == "push") await Handle(ctx.ReadFromJson(AppJson.Default.PushEvent)!);
});                                                       // writes nothing → 200

app.MapWebhook("/hooks/stripe", WebhookSignature.Stripe(stripeSecret),
    async (WebhookContext ctx) => { ...; return Results.Ok(); },
    o => o.SuppressDuplicates());                         // per-route options
```

- Verifiers: `GitHub`, `Stripe`, `StandardWebhooks` (Svix/Clerk/Resend, `whsec_` secrets), `Slack`, `Hmac(o => { o.HeaderName; o.Algorithm; o.Encoding; o.Prefix; o.Secrets; o.DeliveryIdHeader; })`. Every factory takes several secrets for rotation. The timestamp tolerance is 5 minutes (`Stripe(TimeSpan.FromMinutes(10), secret)`).
- Read the body from `ctx.Body`, `ReadAsString()` or `ReadFromJson(JsonTypeInfo)`. Never parse and re-serialize before verification. `ctx.Request.Body` is a rewound copy, so `[FromBody]` and form readers still work.
- A failure is a 401 problem response with a generic message; the reason is logged. Over `MaxBodySize` (5 MiB) is a 413.
- `SuppressDuplicates()` answers a seen delivery id with 200 without running the handler, and forgets the id if the handler throws or returns 5xx. Slack has no id.
- Named verifiers: `builder.AddWebhooks(o => o.AddVerifier("github", WebhookSignature.GitHub(secret)))`, then `app.MapWebhook("/hooks/github", "github", handler)`.
- Existing routes and typed endpoints: `app.UseWebhookVerification()` plus `.RequireWebhookSignature("github")`, or `[RequireWebhookSignature("github")]` with a `WebhookContext` parameter. **Metadata does nothing without `UseWebhookVerification()`.** Prefer `MapWebhook` for new routes.
- Sending: `WebhookSigner.StandardWebhooks(secret).Apply(request, messageId, bodyBytes)`. `WebhookSigner.GenerateSecret()` makes a `whsec_` secret.

## Idempotency keys and content digests

Tier 2 (middleware), with per-endpoint metadata and tier-3 attributes.

```csharp
builder.AddIdempotency();                 // o.Expiration, o.ShouldStoreStatusCode, o.MaxResponseBodyBytes
builder.AddContentDigest();               // o.ResponseDigest = DigestEmission.Always

app.UseContentDigest();                   // BEFORE UseRequestDecompression/UseResponseCompression
app.UseAuthentication();
app.UseAuthorization();
app.UseIdempotency();                     // after routing, AFTER UseAuthorization (the caller is part of the key)

app.MapPost("/payments", Charge).RequireIdempotencyKey();   // no key → 400
app.MapPost("/notes", Save).WithIdempotency();              // honoured if sent
app.MapPost("/firmware", Upload).RequireContentDigest();    // body must carry a verifying Content-Digest
app.MapGet("/snapshot", Get).WithContentDigest();           // response always digested

var payments = group.RequireIdempotencyKey();               // group convention: use the RETURNED builder
```

Tier 3: `[Idempotent]` (Required = true by default, `ExpirationSeconds`), `[DisableIdempotency]`,
`[ContentDigest(RequireRequestDigest = true)]`, `[DisableContentDigest]`.

- Idempotency handles POST/PATCH only. A replay carries `Idempotent-Replayed: true`. Same key with a different body → 422, still running → 409, 5xx/exceptions are not stored. Keep idempotent responses small and non-streamed or they cannot be replayed.
- Content digests: the request body is verified as it streams, and the final read throws on a mismatch, which becomes a 400 problem. Response digests are sent when asked (`Want-Content-Digest`), as a header up to 64KB and as a trailer beyond that. HTTP/1.1 with a declared Content-Length cannot trail. Use `DigestFields.Compute(...)` to set one by hand.

## OAuth loopback sign-in (desktop and CLI)

`Shiny.Net.HttpServer.OAuthLoopback` receives the redirect of an OAuth 2.0 / OpenID Connect sign-in
done in the system browser (RFC 8252 §7.3). It is a one-shot server on an ephemeral `127.0.0.1` port.
**It is not an OAuth client.** The authorization URL, PKCE and the token exchange belong to the
user's OAuth library or their own `HttpClient` code. Do not generate PKCE or token code inside it.

**Tier:** internally it is a tier-0 `OnRequest` server that the class owns. The app maps nothing and
configures no pipeline. It is a standalone class, not something added to the app's own server.

```csharp
using Shiny.Net.HttpServer.OAuthLoopback;

// One call: start, build the URL, open the browser, wait, stop.
var result = await LoopbackCallbackListener.AuthorizeAsync((redirectUri, state) => new Uri(
    $"{authorizeEndpoint}?response_type=code&client_id={clientId}" +
    $"&redirect_uri={Uri.EscapeDataString(redirectUri.AbsoluteUri)}&state={Uri.EscapeDataString(state)}" +
    $"&code_challenge={challenge}&code_challenge_method=S256"));

if (result.IsError) { /* result.Error, result.ErrorDescription */ }
else await ExchangeAsync(result.Code!, verifier);

// Or step by step, when the redirect URI is needed first:
await using var callback = await LoopbackCallbackListener.StartAsync(new() { Timeout = TimeSpan.FromMinutes(5) });
SystemBrowser.Open(Build(callback.RedirectUri, callback.State));
var r = await callback.WaitAsync(ct);
```

Rules when generating it:
- Always put `callback.State` (or the `state` passed to the builder) in the authorization request.
  A callback with a missing or wrong `state` gets a 400 and is ignored. It does not complete the wait.
- With **Duende.IdentityModel.OidcClient**, set `ValidateState = false` (OidcClient owns `state`),
  set `OidcClientOptions.RedirectUri = listener.RedirectUri.AbsoluteUri`, and write an `IBrowser` that
  calls `SystemBrowser.Open(options.StartUrl)` and returns
  `new BrowserResult { ResultType = Success, Response = result.CallbackUri.AbsoluteUri }`. Map a
  `TimeoutException` to `BrowserResultType.Timeout`.
- **MSAL** has its own loopback listener. Do not add this package for `AcquireTokenInteractive`.
- Keep the default `Address` (`127.0.0.1`) and `Port = 0`. Anything that is not loopback throws.
  Set a fixed `Port` only if the provider demands an exact redirect URI.
- Provider refusals (`access_denied`) come back as `result.IsError`, not as exceptions.
  `WaitAsync` throws `TimeoutException` on `Timeout` (default 5 min) and `OperationCanceledException`
  on cancellation.
- `response_mode=form_post` is accepted (`AllowFormPost`, on by default).
- Customise the browser page with `RenderPage` (HTML-encode anything taken from the result) or send
  the browser elsewhere with `RedirectTo`.
- **Never suggest it on iOS, tvOS or Android.** The app is suspended while the browser is in front.
  Use MAUI `WebAuthenticator` with a custom scheme there. `SystemBrowser.Open` throws
  `PlatformNotSupportedException` on those platforms.
- For a machine with no browser (SSH, containers), point to the device authorization grant
  (RFC 8628) instead.

## Content

```csharp
app.UseStaticFiles("./wwwroot", o => o.FallbackFile = "index.html");
app.UseEmbeddedFiles(typeof(App).Assembly, "MyApp.wwwroot");     // packaged / MAUI
app.UseBlazorWebAssembly("./wwwroot");                            // SPA + precompressed + cache policy
app.MapFileBrowser("/files", o => o.RootPath = FileSystem.AppDataDirectory).RequireAuthorization();
```

**Four sources**, all `IStaticFileSource` and all usable with any of the above:
`PhysicalFileSource` (a directory), `EmbeddedFileSource` (loose embedded resources),
`ZipFileSource` (a zip on disk or embedded in the assembly), `CompositeFileSource` (tried in order —
a directory in front of the packaged copy is the development arrangement).

```csharp
// A packaged Blazor publish, zipped into the app - one embedded resource instead of thousands,
// and the paths survive instead of being flattened into the resource name.
app.UseBlazorWebAssembly(
    new ZipFileSource(typeof(App).Assembly, "MyApp.wwwroot.zip")
    {
        PrecompressedEncodings = ["br", "gzip"]   // a publish zips its .br/.gz sidecars too
    }
);

new ZipFileSource("./content/site.zip");            // on disk
new ZipFileSource("./site.zip", "wwwroot");         // zipped with its parent folder
```

- Prefer `ZipFileSource` over `EmbeddedFileSource` for a **publish output**: `EmbeddedFileSource` has
  to un-mangle dotted resource names, which is ambiguous for `site.min.css`, and thousands of loose
  resources inflate the assembly. A zip keeps real paths and stays compressed.
- Unknown file extensions are **not served** by default. Add `ContentTypeOverrides[".x"]` rather than
  turning on `ServeUnknownFileTypes`.
- `MapFileBrowser("/", …)` mounts the browser on the whole site — the shape a "serve this directory"
  CLI wants. Literals still beat its catch-all, so routes mapped alongside it keep answering.
- Uploads: `await foreach (var part in ctx.Request.ReadMultipartAsync(ct))` and
  `part.SafeFileName()` (never `part.FileName` — traversal). `ReadFormAsync` buffers; use it only for
  small fields.
- Downloads: `FileDownloadResult.FromFile(...)` gives ranges, ETags and conditional GETs.

### WebDAV — a directory as a mountable drive

`MapFileBrowser` is a JSON API you drive with curl or your own client. `MapWebDav` speaks the
protocol the operating system already has a client for, so the folder mounts as a drive with no
client code at all — and its browser `GET` is a working file manager. Reach for it whenever the user
says *mount*, *Finder*, *Explorer*, *map a network drive*, *WebDAV*, or wants a **UI** over a
directory; reach for the file browser when they want a JSON API to call from their own code.

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
- A browser `GET` on a collection returns a **file manager**: listing with sizes and times,
  breadcrumbs, drag-and-drop upload (`PUT`, folders walked and recreated), new folder (`MKCOL`),
  rename (`MOVE`, `Overwrite: F`) and delete (`DELETE`), plus a download button per file. It offers
  only what the options allow — read-only renders a listing with no buttons — and it is one
  self-contained response with nothing fetched from outside. Entries and the link to the parent are
  absolute, so the tree is walkable with or without a trailing slash on the mount URL.
  `DirectoryBrowsing = false` turns the page off entirely (a browser `GET` then answers 405). This
  is the same page `shinyhttpserver` serves — say "open the mount URL in a browser" when a user asks
  for a UI over a directory; there is no other file-manager UI in this library.
- The mount is excluded from the OpenAPI document — do not try to describe it.
- **Not one directory?** Implement `IWebDavFileSystem` and set `o.FileSystem` instead of `RootPath`
  — several roots side by side, platform-API folders, a photo library. Paths arrive relative,
  `/`-separated, root `""`, already stripped of `..`/dotfiles/`Filter` misses. `WebDavEntry.Name` is
  the URL segment and `DisplayName` what the client shows. Refuse with
  `throw new WebDavException(403)`; `UnauthorizedAccessException`→403,
  `FileNotFound`/`DirectoryNotFound`→404, other `IOException`→409. Let the 413 the write stream throws
  propagate. `OpenReadAsync` returning a seekable stream wins over the entry's length — buffer
  anything transcoded on the fly. Do not copy the data into a temp directory to use `RootPath`.

### CalDAV & CardDAV — calendars and contacts the phone's own apps sync

Reach for it when the user wants their app's events or contacts to show up in iOS or macOS Calendar or
Contacts, Thunderbird or DAVx⁵, or says *CalDAV*, *CardDAV*, *subscribe*, *sync calendar*. Tier 1: one
call mounts the whole protocol (package `Shiny.Net.HttpServer.CalDav`, built on the WebDAV package).

```csharp
app.MapCalDav("/dav", o =>
{
    o.CalendarStore = new FileCalendarStore(Path.Combine(FileSystem.AppDataDirectory, "calendars"));
    o.AddressBookStore = new FileAddressBookStore(Path.Combine(FileSystem.AppDataDirectory, "contacts")); // either may be null
})
.RequireAuthorization();   // Basic auth; the user name IS the principal
```

- One mount serves both protocols and also maps `/.well-known/caldav` and `/.well-known/carddav` (HttpServer overload only; otherwise call `app.MapCalDavWellKnown("/dav")`). On iOS: Settings → Calendar → Accounts → Add CalDAV Account → device address plus the Basic credentials.
- **Always require authorization and use TLS.** The principal is `User.Identity.Name`; other principals' URLs get 403. `DefaultPrincipal` exists only for a single-user mount with no auth.
- Serving existing data (device calendar, SQLite): implement `ICalendarStore` / `IAddressBookStore` — collections of text objects keyed by name, each with a quoted strong ETag that changes whenever the text does. `PutObjectAsync` gets a validated `DavObjectWrite` (with `.Component` already parsed); return the new ETag, or `null` if the text was not stored byte for byte. Refuse with `throw new WebDavException(403)`. Set `IsReadOnly` on read-only calendars.
- Do not hand-roll sync tokens, ctags, UID checks or ETag preconditions; the mount does them for any store.
- Parse or build iCalendar/vCard with `ContentComponent.Parse(text)`, `GetComponents("VEVENT")`, `GetProperty("SUMMARY")?.GetText()` and `ToString()` (folds lines). It is not a full calendar library.
- Recurrence: DAILY/WEEKLY/MONTHLY/YEARLY with BYDAY/BYMONTHDAY/BYMONTH/EXDATE/overrides are expanded. Anything else is treated as matching. Queries read every object, which is fine for thousands.

### Resumable uploads (tus)

A plain upload restarts from zero when the connection drops. `MapTus` speaks tus 1.0.0, so any tus
client (tus-js-client, Uppy, TusDotNetClient, TUSKit) resumes from the last byte the server kept.
Reach for it whenever the user says *resumable*, *large upload from a phone*, *resume after
disconnect*, *tus*, or *chunked upload*. Tier 1: seven raw routes mapped in one call (package
`Shiny.Net.HttpServer.Tus`).

```csharp
app.MapTus("/files", o =>
{
    o.Store = new DiskTusStore(Path.Combine(FileSystem.AppDataDirectory, "uploads")); // required
    o.MaxSize = 4L * 1024 * 1024 * 1024;   // Tus-Max-Size
    o.Expiration = TimeSpan.FromDays(1);    // sliding; unfinished uploads only
    o.OnBeforeCreateAsync = ctx =>
    {
        if (!ctx.Metadata.ContainsKey("filename")) ctx.Reject(400, "filename required");
        return ValueTask.CompletedTask;
    };
    o.OnUploadCompleteAsync = async ctx =>
    {
        await using var file = await ctx.OpenReadAsync();   // runs once, never for partials
    };
})
.RequireAuthorization();

app.UseCors(p => p.WithOrigins("https://app.example.com").WithTusHeaders()); // browser clients
```

- Do not hand-roll chunked or resumable uploads with `MapPost`. Use this.
- Each PATCH is bounded by `Limits.MaxRequestBodySize` (30 MB). Tell the client to use a smaller
  chunk size (tus-js-client `chunkSize`), or raise the limit.
- Metadata values are client input: `ctx.Metadata["filename"]` is text, never a path to write to.
- A custom `ITusStore.AppendAsync` must keep the bytes when the stream ends early and roll back
  when the stream throws. Throw `TusException(409)` for a wrong offset and `TusException(404)` for a
  missing upload. The endpoint already serialises requests per upload, so the store needs no lock.
- `LockReleaseTimeout = TimeSpan.Zero` answers 423 instead of taking over a stalled request.
- `RemoveExpiredUploadsAsync()` on the returned builder runs cleanup from a platform background job.
- The routes are excluded from OpenAPI. Do not describe them.

## Realtime

```csharp
// WebSockets — the handler owns the socket; loop inside it
app.MapGet("/ws", async ctx =>
{
    if (!ctx.Request.IsWebSocketRequest()) { ctx.Response.StatusCode = 400; return; }

    await using var socket = await ctx.AcceptWebSocketAsync();
    while (await socket.ReceiveAsync(ctx.RequestAborted) is { } msg)
        await socket.SendAsync(msg.Text, ctx.RequestAborted);
});

// SSE
app.MapGet("/events", ctx => ctx.SendEventsAsync(async stream =>
{
    while (!stream.Aborted.IsCancellationRequested)
    {
        await stream.SendAsync($"tick {DateTime.UtcNow:O}");
        await Task.Delay(1000, stream.Aborted);
    }
}));
```

### Talking to every socket at once

A handler owns one socket and cannot reach the others. `IWebSocketRegistry` is the list, the groups
and the dead-socket cleanup, so an app does not hand-roll all three.

```csharp
builder.AddWebSocketRegistry();

app.MapGet("/ws", async ctx =>
{
    await using var tracked = await ctx.AcceptTrackedWebSocketAsync(cancellationToken: ctx.RequestAborted);
    tracked.JoinGroup("kitchen");

    while (await tracked.ReceiveAsync(ctx.RequestAborted) is { } message)
        await registry.SendToGroupAsync("kitchen", message.Text, ctx.RequestAborted);
});

await registry.BroadcastAsync("the door unlocked");        // returns how many were reached
await registry.SendToUserAsync("ada", "your build finished");
```

- A broadcast **never throws for a dead peer**: that socket is dropped from the registry and the
  count comes back one lower. Sends run concurrently, so one stalled client does not hold up the rest.
- Disposing the `TrackedWebSocket` untracks and closes it, so the registry needs no cleanup pass.

### Compression and keepalive (defaults, worth knowing)

- **permessage-deflate is on** when the client offers it, with no context takeover in either
  direction — each message compresses on its own, so a socket costs no persistent zlib window.
  `EnablePerMessageDeflate = false` turns it off; `CompressionThreshold` (256 bytes) is the floor
  below which deflate makes a message bigger.
- **Keepalive pings every 30s**, socket torn down after 2 unanswered. This is what notices a phone
  that lost signal — a dropped mobile connection goes quiet rather than closing. Set
  `KeepAliveInterval = null` to switch it off (do that in tests that assert on frames).

## Switchboard — SignalR-style calls

`Shiny.Net.HttpServer.Switchboard` (server) and `Shiny.Net.HttpServer.Switchboard.Client` (.NET
client). **Reach for it whenever the user asks for SignalR, a hub, `IHubContext`, or "push to clients
and let them call back"** — ASP.NET Core SignalR cannot run in MAUI, and hand-rolling SSE plus
`IWebSocketRegistry` rebuilds half of this badly. Use raw SSE only for one-way feeds; use raw
WebSockets only for high-rate binary traffic.

Tiers: a switchboard class is **tier 3** (source-generated dispatch, like `[Route]` classes);
`MapSwitchboard` is **tier 1**; `ISwitchboardFilter` is **tier 2** (switchboard middleware).

Vocabulary: switchboard = hub, **line** = connection (`Context.LineId`, `Clients.Line(id)`),
**operator** = `IHubContext` (`IOperator<TBoard>` / `IOperator<TBoard, TClient>`), **hang up** = abort.
Not wire-compatible with SignalR — never generate a SignalR client against it, or vice versa.

```csharp
using Shiny.Net.HttpServer.Switchboard;

public interface IChatClient                      // what the server calls on clients
{
    Task MessageReceived(ChatMessage message);
    Task<bool> ConfirmDelete(string room);          // Task<T> = a client result (server waits)
}

public class ChatBoard(IRoomStore rooms) : Switchboard<IChatClient>   // or : Switchboard (untyped)
{
    public override Task OnConnectedAsync() => Clients.Caller.MessageReceived(…);
    public override Task OnDisconnectedAsync(DisconnectContext d) => …;  // d.Reason: ClientClosed/Dropped/HungUp/ServerShutdown/Faulted

    public Task JoinRoom(string room) => Groups.AddToGroupAsync(Context.LineId, room);
    public Task Send(string room, string text) => Clients.Group(room).MessageReceived(new(room, Context.UserIdentifier!, text));
    public IReadOnlyList<RoomInfo> Rooms() => rooms.List();                                   // returns a value
    public async IAsyncEnumerable<ChatMessage> History(string room, [EnumeratorCancellation] CancellationToken ct) { … }  // streams

    [Authorize(Roles = "admin")]
    public async Task DeleteRoom(string room)
    {
        if (!await Clients.Caller.ConfirmDelete(room)) throw new SwitchboardException("Cancelled");  // message reaches the client
    }
}

builder.AddSwitchboard(o => o.ResumeWindow = TimeSpan.FromSeconds(30));
app.MapSwitchboard<ChatBoard>("/chat").RequireAuthorization();

// outside a switchboard
public class Shipped(IOperator<ChatBoard, IChatClient> board)
{
    public Task Handle(Order o) => board.Clients.User(o.CustomerId).MessageReceived(…);
}
await board.Lines.HangUpUserAsync("mallory", "Banned");     // token dies; client does not reconnect
```

Client:

```csharp
using Shiny.Net.HttpServer.Switchboard.Client;

var line = new SwitchboardLineBuilder()
    .WithUrl("https://host/chat", o => o.AccessTokenProvider = () => GetTokenAsync())
    .WithJson(ChatJson.Default)                 // the same JsonSerializerContext as the server
    .WithAutomaticReconnect()
    .Build();

line.On<ChatMessage>("MessageReceived", m => …);
line.On<string, bool>("ConfirmDelete", room => AskAsync(room));      // answers a client result
line.Reconnected += (_, e) => { if (e.NewLine) /* rejoin groups */; };
line.Closed += (_, e) => …;                                          // e.Reason: ClientClosed/HungUp/ServerClosed/Dropped/RetriesExhausted/Faulted
await line.StartAsync();
var n = await line.InvokeAsync<int>("Count");
await foreach (var m in line.StreamAsync<ChatMessage>("History", "general")) { }

// Typed client from shared contract interfaces:
[SwitchboardClient<IChatBoard, IChatClient>] public partial class ChatBoardClient;
// → calls as methods, IChatClient methods as events, Task<T> ones as a handler property, Register(IChatClient)
```

Rules that matter when generating code:
- **Every argument/result type needs JSON metadata** on both sides (`[JsonSerializable]` in a context;
  pass it to `WithJson` on the client). Primitives are built in. Never suggest reflection.
- One instance **per call** — keep state in `Context.Items` (safe without locks while
  `MaximumParallelInvocationsPerLine` is 1, the default) or a service, never in fields.
- Methods are called by name: **no overloads** (SWB003); rename with `[LineMethod("x")]`, hide with
  `[NotALineMethod]`. Parameters: JSON by position, plus `CancellationToken`, `CallerContext`,
  `[FromServices] T`. No `IAsyncEnumerable<T>` parameters (client streaming is not supported).
- Client results only from `Clients.Caller` / `Clients.Line(id)` (typed: `Task<T>` on a group throws
  `NotSupportedException`). They work from inside a switchboard method — no parallelism setting needed.
- A dropped line is resumed within `ResumeWindow` with its id, groups, `Items` and missed messages;
  after that the client gets a **new line** (`Reconnected.NewLine`) and must rejoin groups.
- `[Authorize]` on methods is enforced by the switchboard itself; `[AllowAnonymous]` on a method cannot
  lift a class requirement (SWB007).
- Contract for a client in another repo: `[ExportContract(Namespace = "X.Contracts")]` on the board
  plus `<SwitchboardContractsOutputPath>` in the server csproj writes one self-contained file.
- Name clash: code inside a `Shiny.Net.HttpServer.*` namespace must qualify the `Switchboard` base
  class (CS0118). A file importing both server and client namespaces must alias `SwitchboardException`.
- In-process only (no backplane), .NET client only (no JS client).

## Timeouts, caching and conditional requests

```csharp
builder.AddRequestTimeouts(o =>
{
    o.DefaultPolicy = new RequestTimeoutPolicy(TimeSpan.FromSeconds(30));
    o.AddPolicy("reports", TimeSpan.FromMinutes(2));
});
builder.AddOutputCache(o => o.AddPolicy("lists", new OutputCachePolicy(TimeSpan.FromSeconds(30))
{
    VaryByHeaders = ["Accept"]
}));

app.UseRequestTimeouts();
app.UseOutputCache();

app.MapGet("/report", Handler).WithRequestTimeout(TimeSpan.FromSeconds(10));
app.MapGet("/stream", Handler).DisableRequestTimeout();     // SSE, downloads, upgrades
app.MapGet("/list",   Handler).CacheOutput(TimeSpan.FromSeconds(10));
app.MapGet("/live",   Handler).NoOutputCache();
```

Tier 3 spells the same things as attributes: `[RequestTimeout(2000)]`, `[RequestTimeout("reports")]`,
`[DisableRequestTimeout]`, `[OutputCache(Seconds = 30)]`, `[OutputCache("lists")]`, `[NoOutputCache]`.

- A timeout is **delivered as cancellation** on `ctx.RequestAborted`. A handler that ignores its
  token still runs to completion — it just does so after the client has been answered. Pass the token
  into the slow thing.
- Output caching stores **GET/HEAD 200s only**, never a response with `Set-Cookie`, and skips
  authenticated callers unless the policy says otherwise (`AllowAuthenticated`) — a cache keyed on
  the URL cannot tell two users apart. A streamed response (one that called `StartAsync`) is passed
  through untouched.
- On a device the win is **battery, not bandwidth**: the expensive part is the database read or the
  sensor poll behind the endpoint.

Conditional requests for handlers that are not serving a file:

```csharp
app.MapGet("/items/{id}", async ctx =>
{
    var item = await store.GetAsync(ctx.Request.RouteValues["id"]!, ctx.RequestAborted);

    // Writes ETag/Last-Modified, answers 304 or 412 itself, and returns true when it did.
    if (await ctx.TryCompleteConditionalAsync(EntityTag.FromContent(item.Version), item.Updated))
        return;

    await ctx.Response.WriteJsonAsync(item, AppJson.Default.Item, ctx.RequestAborted);
});
```

`CheckPreconditions` is the same evaluation without writing anything; `SetETag`, `SetLastModified`,
`SetCacheControl` and `SetNoStore` are the response-side helpers. A 304 saves the serialisation as
well as the bytes.

## Diagnostics — health checks and telemetry

```csharp
builder.AddHealthChecks()
    .AddServerCheck()                                    // is the listener actually up
    .AddCheck("database", async ct => await db.CanConnectAsync(ct)
        ? HealthCheckResult.Healthy()
        : HealthCheckResult.Unhealthy("no connection"), "ready")
    .AddCheck<SyncHealthCheck>("sync", tags: ["ready"]);  // resolved from the container

app.MapHealthChecks();                                   // /health
app.MapHealthChecks("/health/live", "live");             // filtered by tag
app.MapHealthChecks("/health/ready", "ready");
```

- Checks run **concurrently**, each with a timeout (5s default). A probe that hangs is worse than one
  that answers Unhealthy.
- Healthy and Degraded both answer **200**; Unhealthy answers **503**. Degraded means "serving, with
  a caveat", and a load balancer that pulls the instance over it turns a caveat into an outage.
- The report is written with `Utf8JsonWriter`, so no `JsonSerializerContext` is needed for it.
- `IncludeDetails = false` for an endpoint reachable through a tunnel — the entry list is a free
  inventory of the app's dependencies.

```csharp
app.UseTelemetry();          // one Activity + one duration measurement per request

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(HttpServerTelemetry.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(HttpServerTelemetry.MeterName));
```

- Instruments follow the OpenTelemetry HTTP semantic conventions — `http.server.request.duration`,
  `http.server.active_requests`, `http.server.active_connections` — so an ASP.NET dashboard reads
  this server without being told about it.
- The span is renamed to `GET /users/{id}` once routing has chosen an endpoint, never the raw path.
- `ContinueIncomingTrace` is on by default and should be **turned off for a tunnelled server**: a
  caller who picks the trace id can graft spans onto someone else's trace.
- `RecordUrl` is off: paths carry identifiers and query strings carry secrets.

### W3C access logs

```csharp
app.UseW3CLogging(o =>
{
    o.LogDirectory = FileSystem.AppDataDirectory;      // must be writable — the default is not, in MAUI
    o.Fields = W3CLoggingFields.Default | W3CLoggingFields.Route;
    o.ShouldLog = ctx => ctx.Request.Path != "/health";
});
```

One line per request in the format IIS writes, which every log analyser reads. Suggest it when the
ask is "a log I can look at later" and there is nowhere to ship telemetry to — a device, an
appliance, a customer's machine.

- The line is queued, never written on the request path. A full queue **drops** lines, counts them,
  and writes `#Remark: N line(s) dropped` into the file — a slow disk must not become a slow server.
- Files roll at `FileSizeLimit` and are pruned to `RetainedFileCountLimit`. There is no unbounded
  setting on purpose.
- `W3CLoggingFields.Default` excludes `Cookie`. Do not add it without a reason — a log file gets
  copied around and that header is a session token.
- `x-route` and `x-connection-id` are extensions (the `x-` prefix is the format's own rule) and are
  what make the log useful for finding a slow *endpoint* rather than a slow URL.
- `IW3CLogWriter` is the seam for sending lines somewhere other than a file.

## Antiforgery, security headers and HTTPS redirect

```csharp
builder.AddAntiforgery();
app.UseAntiforgery();
app.UseSecurityHeaders(o => o.ContentSecurityPolicy = SecurityHeaderOptions.SelfOnlyContentSecurityPolicy);
app.UseHttpsRedirection();          // port taken from the first TLS endpoint

// issue the pair to the page, then echo the request token back in X-CSRF-TOKEN
var tokens = ctx.GetRequiredService<IAntiforgery>().GetTokens(ctx);
```

- The check applies to unsafe methods **only when the request carries cookies**. CSRF is an attack on
  ambient credentials; a caller holding a bearer token attaches it deliberately and an attacker's
  page cannot. `[ValidateAntiforgery]` / `.ValidateAntiforgery()` forces the check anyway;
  `[DisableAntiforgery]` / `.DisableAntiforgery()` exempts a webhook.
- **This matters for the file browser and WebDAV UI**: putting either behind cookie authentication
  without antiforgery is a delete button any page on the internet can press.
- The token is read from a header, not a form field — reading a form would consume the body. For a
  posted form, read the form yourself and call `ValidateToken(ctx, form["__RequestVerificationToken"])`.
- **HSTS is off by default and should stay off** for a LAN or loopback server: a browser remembers it
  for the whole host, and a device that serves plain HTTP tomorrow is locked out of itself.

### Real certificates on a public host (ACME)

Host configuration (builder setup plus TLS options), beside tier 2 — the only thing it adds to the
pipeline is the HTTP-01 challenge middleware. Package `Shiny.Net.HttpServer.Acme`.

```csharp
builder.Options.Listen(IPAddress.Any, 80);                               // HTTP-01 is ALWAYS port 80
builder.Options.Listen(IPAddress.Any, 443).Https = new HttpsOptions();   // no certificate => ACME's
builder.AddAcme(o =>
{
    o.Domains = ["example.com", "www.example.com"];
    o.Email = "ops@example.com";
    o.AcceptTermsOfService = true;                          // required; never default it silently
    o.DirectoryUrl = AcmeDirectories.LetsEncryptStaging;    // staging until it works (rate limits)
    // o.Challenges = AcmeChallengeTypes.TlsAlpn01;         // when only 443 is reachable
    // o.ExternalAccountBinding = new(kid, hmacKey);        // ZeroSSL / commercial CAs
    // o.StorePath = "/var/lib/myapp/acme";                 // put on a volume in containers
});
// HTTP/3: await app.ListenHttp3Async(o => { o.Port = 443; o.UseAcme(app); });
```

- Only for hosts the CA can reach at every name (public DNS, port 80 or 443). **Never generate this
  for a phone/MAUI app** — use `ServerCertificate` + pinning or a tunnel.
- An HTTPS endpoint with no `Certificate`/`CertificateSelector` gets the ACME certificate; renewals
  hot-swap per handshake, no restart. `app.GetAcmeCertificateManager()` exposes `CertificateChanged`,
  `RenewAsync`, `EnsureCertificateAsync`.
- Register `AddAcme` before auth/IP-filter/host-filter middleware that would reject the CA's anonymous GET.
- No wildcards (DNS-01 not supported). Persist `StorePath` — re-ordering on every start hits rate limits.
- The core hooks it uses are public: `HttpsOptions.CertificateContextSelector` (per-handshake certificate
  + chain, also on `Http3Options`) and `HttpsOptions.ChallengeResponder` (sees SNI + ALPN first).
- Metrics on the server meter: `shiny.acme.certificate.issuances` (result) and
  `shiny.acme.certificate.expiry` (days, observable). `manager.IssuanceFailed` fires on every failed order.

**Many hosts, one certificate each, added at runtime** (an edge server, a multi-tenant host) — use
`AcmeCertificateRegistry`, not several managers:

```csharp
builder.AddAcmeRegistry(o =>
{
    o.Email = "ops@example.com";
    o.AcceptTermsOfService = true;
    o.DirectoryUrl = AcmeDirectories.LetsEncryptStaging;
    // o.FallbackCertificate = ServerCertificate.Create(...);   // unknown SNI gets this, not a failed handshake
});
var app = builder.Build();
var acme = app.GetAcmeCertificateRegistry();       // no container: new AcmeCertificateRegistry(opts); app.UseAcme(acme);
// HTTP/3: o.UseAcme(acme) on Http3Options

acme.Set("grafana", ["grafana.example.com"]);      // add/rename; loads a stored cert; NEVER orders
await acme.IssueAsync("grafana", ct);              // the only first issuance; throws AcmeException
await acme.ImportAsync("grafana", fullChainPem, keyPem, ct);   // or X509Certificate2 / pfx bytes
await acme.RemoveAsync("grafana");
acme.IssuanceFailed += (_, e) => ...;              // e.Name, e.Exception, e.IsRenewal
```

- Do not generate code that calls `IssueAsync` automatically on `Set`/startup/discovery: first issuance is
  meant to be explicit, and a failed one is deliberately not retried (CA rate limits). Renewal of an
  issued certificate is automatic.
- SNI: exact name → `*.` wildcard on a served cert → `FallbackCertificate` → refused. One HTTP-01
  middleware and one TLS-ALPN-01 hook serve every entry, including ones added after start.
- `acme.Entries` / `GetEntry(name)` give `State` (`None`/`Issuing`/`Issued`/`Failed`), `NotAfter`,
  `NextRenewal`, `LastError` for a status screen.

## Proxying to another server

Needs `Shiny.Net.HttpServer.Proxy` and `using Shiny.Net.HttpServer.Proxy;` — it is **not** in the core
package. This is tier 2 (routes): a proxy route is an ordinary route with a generated handler, so
middleware, authentication and rate limiting apply to it exactly as they do to anything else.

**One destination** — reach for this first, and only escalate when the ask names more than one:

```csharp
app.MapProxy("/api/{*path}", "https://api.example.com");
app.MapProxy("/printer/{*path}", "http://192.168.1.50", o => o.RewriteHost = false);
```

Bodies stream both ways, `X-Forwarded-*` describe the original caller, an unreachable upstream is a
**502**, one that will not answer is a **504**, and a WebSocket (or any HTTP/1.1 upgrade) is forwarded
end to end — `o.ForwardUpgrades = false` turns that off.

**A cluster** when there is more than one instance behind the route:

```csharp
app.MapProxy("/api/{*path}", cluster =>
{
    cluster.AddDestination("a", "https://a.internal");
    cluster.AddDestination("b", "https://b.internal");

    cluster.LoadBalancing = LoadBalancingPolicy.PowerOfTwoChoices;  // default; also RoundRobin,
                                                                   // LeastRequests, Random, First
    cluster.HealthCheck.Active.Enabled = true;                      // off by default — probes cost battery
    cluster.HealthCheck.Active.Path = "/health";
    cluster.SessionAffinity.Mode = SessionAffinityMode.Cookie;      // or Header
});
```

Passive health (on for a cluster, off for the single-destination call) takes a destination out after
`FailureThreshold` consecutive transport failures for `ReactivationPeriod`. When every destination is
out, the route answers **503**.

**Transforms** instead of `BeforeSend`/`AfterReceive` whenever the change is a path, query or header:

```csharp
cluster.Transforms
    .RemovePathPrefix("/api")
    .SetQueryValue("tenant", "acme")
    .SetRequestHeader("X-Api-Key", key)
    .RemoveResponseHeader("Server");
```

**From configuration** when the ask is routes that change without a rebuild. The shape matches YARP's,
including `{**rest}` catch-alls, and it is parsed by hand rather than reflection-bound, so it stays
AOT-clean:

```csharp
builder.AddReverseProxy(configuration.GetSection("ReverseProxy"));
// no container: var proxy = app.MapReverseProxy(configuration.GetSection("ReverseProxy"));
```

```json
{ "ReverseProxy": {
    "Routes":   { "api": { "ClusterId": "backend", "Match": { "Path": "/api/{**rest}" },
                           "Transforms": [ { "PathRemovePrefix": "/api" } ] } },
    "Clusters": { "backend": { "LoadBalancingPolicy": "LeastRequests",
                               "Destinations": { "d1": { "Address": "https://a.internal" } } } } } }
```

Reload is applied to the running server: routes swap atomically, clusters keep their health state, and
routes mapped in code are untouched.

- **Do not put a proxy on a tunnel without authentication and rate limiting in front of it**, and keep
  the destination fixed rather than reading it from the request.
- `HttpForwarder.ForwardAsync(ctx, "http://…")` forwards from inside a hand-written handler.
- Response trailers are relayed (h2/h3 trailing HEADERS, h1 chunked trailers) and `TE: trailers` is
  forwarded, so **gRPC proxies**: set `o.RequestVersion = HttpVersion.Version20; o.VersionPolicy =
  HttpVersionPolicy.RequestVersionExact;` for an h2c upstream, and the caller must use h2/h3.
- Metrics on the server meter: `shiny.proxy.upstream.request.duration` (to response headers, by
  `server.address`) and `shiny.proxy.upstream.errors` (`error.type` = `ProxyError` kind).

## Tunnelling

```csharp
// Zero-account public HTTPS from a phone — pinggy by default, nothing to configure
builder.AddQuickTunnel();
// then, from a button: await tunnel.StartAsync();

// A host you own
builder.AddSshTunnel(o =>
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
- Hosting the relay with a real key registry: `RelayServerOptions.Authorize` is async —
  `(request, ct) => ValueTask<string?>` returning the subdomain to grant or `null` to refuse. Pass
  `ct` into the lookup (it carries `AuthorizeTimeout`, 15s by default), and do not catch-and-grant:
  a throw already refuses the registration and is logged. Tag the grant with `request.State = keyId`
  rather than keeping the token; `relay.TunnelConnected` / `TunnelDisconnected` report it, `relay.Tunnels`
  lists it, and revoking is "make `Authorize` refuse, **then** `await relay.DisconnectAsync(t =>
  Equals(t.State, keyId))`" — in the other order a reconnecting client is simply let back in.
- `AzureRelay` is deliberately **not** AOT-clean; do not suggest it for a trimmed/AOT app.
- `Shiny.Net.HttpServer.Tunnels` supervises the vendor agents — `cloudflared`, `ngrok`,
  `tailscale` — for hosts that can start a process. **Not on a phone:** iOS forbids it outright, so
  on mobile the answer is still the SSH provider or the relay.

  ```csharp
  await server.StartAsync();                       // an agent forwards to a bound port
  await using var tunnel = new CloudflareTunnel(new CloudflareTunnelOptions());
  var url = await tunnel.StartAsync(server);       // reads the port off the server

  // or with a host: registered after AddHttpServer, so the port exists when the agent starts
  builder.AddCloudflareTunnel();          // AddNgrokTunnel() / AddTailscaleFunnel()
  ```

  A missing binary throws `TunnelAgentException` naming what to install; an agent that dies during
  startup fails immediately rather than waiting out the timeout. A token-less Cloudflare tunnel is a
  quick tunnel — new hostname every run.

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

## Early Hints (103) and other 1xx responses

Tier 1 (raw delegate) — a response API usable from any handler, middleware or typed endpoint.
Send `Link` preloads *before* doing slow work, so the browser fetches CSS/JS/fonts in parallel.
Call it **first** in the handler — after the response has started it throws `InvalidOperationException`.

```csharp
app.MapGet("/", async ctx =>
{
    await ctx.Response.SendEarlyHintsAsync(
        "</app.css>; rel=preload; as=style",
        "</app.js>; rel=preload; as=script");

    var model = await LoadAsync(ctx.RequestAborted);            // slow part
    ctx.Response.Headers.Append(HeaderNames.Link, "</app.css>; rel=preload; as=style"); // repeat on final
    await ctx.Response.WriteTextAsync(Render(model), "text/html; charset=utf-8");
});
```

- Returns `ValueTask<bool>`: `false` (nothing sent) on HTTP/1.0 — do not treat that as an error.
- General form: `SendInformationalResponseAsync(StatusCodes.Status102Processing, headers)`; any 1xx except 101.
- Never put CR/LF, `Content-Length` or `Transfer-Encoding` in the headers (throws).
- `HttpClient` (and the in-memory test client) discard 1xx — assert on the final response, or read raw bytes.
- Output-cache hits send no hints; the reverse proxy does not forward an upstream 103.

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

### A remote MCP server a client must authenticate to

An MCP client that meets a bare 401 has no idea where to log in. RFC 9728 is the protocol's answer:
the challenge names a metadata document, and the document names the authorization server.

```csharp
builder.AddMcpProtectedResource(o =>
{
    o.AuthorizationServers.Add("https://login.example.com");
    o.ScopesSupported.Add("mcp:tools");
    o.Resource = "https://device.example.com/mcp";     // omit to derive it from the request host
});

app.MapMcp().RequireAuthorization();
app.MapMcpProtectedResource();      // /.well-known/oauth-protected-resource[/mcp]
```

The 401 then carries `WWW-Authenticate: Bearer resource_metadata="…"`. Validating the token itself is
still `AddJwtBearer(...)`, and its audience should be the same `Resource` published here. Leave
`Resource` null for a tunnelled server whose public address is new every run.

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
- `PATCH` is RFC 7396 merge patch (an explicit `null` **removes** the member) — or RFC 6902 JSON Patch when
  `Content-Type` is `application/json-patch+json`; same If-Match and scope checks either way.
- Mapping `DocumentEndpoints.Stream` on a provider without change monitoring is a startup error.

## tvOS

tvOS is **not** MAUI — MAUI does not target the platform. A tvOS app is a native app on
`net10.0-tvos` with a `UIApplicationDelegate`, and it references `Shiny.Net.HttpServer` the way any
other project does; the core is plain `net10.0` and needs nothing special.

Rules that matter when generating a tvOS app:

- **`NSLocalNetworkUsageDescription` in `Info.plist` is mandatory.** tvOS 16+ gates serving on the
  local network exactly as iOS 14+ does. Without it the listener binds, reports `Running`, and
  nothing can reach it — and a TV has no browser to test with, so nothing reveals the cause.
- **Bind `IPAddress.Any`, not loopback.** Nothing on the device itself will ever consume the server.
- **Do not use `Shiny.Net.HttpServer.Mobile`** — it does not target tvOS yet. Start the server from
  `WillEnterForeground` / `OnActivated` and stop it in `DidEnterBackground`. tvOS suspends a
  backgrounded app exactly as iOS does, so there is no keep-serving option to offer.
- **Do not reference `Microsoft.Extensions.Logging.Console`.** It P/Invokes `GetStdHandle` and
  `GetConsoleMode`, which fails the *native link* on tvOS — the C# compiles clean and clang then
  stops with "Undefined symbols for architecture arm64". The server resolves `ILoggerFactory`
  optionally, so registering no logging provider at all is supported.
- **Do not use the cloudflared / ngrok / Tailscale agents.** tvOS forbids process creation; they
  throw. Use the SSH or Azure Relay providers if a tunnel is needed.
- **No durable storage.** Read-only bundle plus a purgeable Caches directory — no Documents. Serve
  static files from the bundle or a `ZipFileSource`, and do not put a DocumentDb file anywhere you
  need to survive a relaunch.
- **Full AOT, no JIT.** Prefer generated typed endpoints and a `JsonSerializerContext`; nothing can
  fall back to reflection.

## MAUI specifics

### The manifest entries, which fail silently when missing

- iOS: `NSLocalNetworkUsageDescription` in `Info.plist` — without it the app is denied silently.
  Add every browsed service type to `NSBonjourServices` as well if you use discovery.
- Mac Catalyst: `com.apple.security.network.server` entitlement — without it the bind is refused.
- Android: `android.permission.INTERNET`; plus `FOREGROUND_SERVICE`,
  `FOREGROUND_SERVICE_DATA_SYNC` and (API 33+) `POST_NOTIFICATIONS` for background serving.

`LocalNetworkAccess.Check()` (in `Shiny.Net.HttpServer.Mobile`) reads the bundle or manifest and
reports which are missing, because none of these produce an error you could diagnose from. Missing
entries that stop the server being reachable come back in `Problems` (`CanServe` is then false);
things that only matter for discovery come back in `Notes`. The one it cannot check is the Mac
Catalyst entitlement — that is not in `Info.plist` and is not readable from inside the process, so on
Catalyst it is always reported as a note to go and verify by hand.

### Lifecycle — the server has to follow the app and the network

```csharp
services.AddShinyHttpServer(
    http =>
    {
        http.Options.Address = IPAddress.Any;

        http.AddHttpServerLifecycle(o =>
        {
            o.BackgroundMode = BackgroundServerMode.Stop;      // or KeepAlive
            o.RestartOnConnectivityChange = true;              // default

            o.RestartAttempts = 3;                             // default
            o.RestartRetryDelay = TimeSpan.FromSeconds(5);     // default, doubling
            o.MaxRestartRetryDelay = TimeSpan.FromSeconds(30); // default
        });
    },
    autoStart: false
);
```

Needs a Shiny host (`UseShiny()` in `MauiProgram`) — that is what delivers the platform callbacks. It registers
Shiny's `IConnectivity` itself — do not generate a separate `services.AddConnectivity()` for it.

- **iOS suspends the app within seconds of backgrounding**, and no background mode legitimately keeps
  a listener answering. `Stop` makes that visible: `IsRunning` goes false, the UI can say so, and
  clients get a refused connection instead of a hang.
- **Android `KeepAlive` starts a foreground service** with a permanent notification, which is the
  only supported way to hold a socket open in the background. That notification is the deal.
- **iOS `KeepAlive` restores the server on resume** instead. It cannot keep the listener open, so it
  does the other useful thing: the server is left running as the app goes away (a few seconds is
  often enough to finish the request in flight) and is *restarted* when the app comes back. This
  matters because the suspension takes the socket while `IsRunning` goes on saying `true`, so an app
  that only re-checked `IsRunning` would find nothing wrong and serve nothing — and calling
  `StartAsync()` yourself would not help either, since it is idempotent and agrees with the stale
  state. `RestartAsync()` is what fixes it, and this does it for you.
- **This is not `AlwaysStartOnForeground`.** A server that was off when the user left stays off; only
  one that was running is put back. Set `AlwaysStartOnForeground = true` if you want it on at every
  resume regardless — that overrides a toggle the user switched off, so make it a deliberate choice.
- **Both platforms follow the server, not just the app's transitions.** On Android, stopping the
  server while the app is backgrounded takes the notification down with it and starting it brings
  the service up, so Android does not reclaim the process and kill the listener; on iOS the same
  transitions decide whether the resume restores it. Nothing to call — the package tracks
  `HttpServer.StateChanged` — so an app whose server is a toggle does not need its own bookkeeping.
- A phone's address changes when it moves. The lifecycle package rebinds on connectivity changes;
  the core has the same thing without Shiny.Core via `options.RebindOnNetworkChange`, and raises
  `server.NetworkAddressesChanged` either way so a QR code or advertisement can be refreshed.
  Binding to `IPAddress.Any` survives an address change; binding to a specific address does not. The
  rebind retries on a network that is still coming up — see
  [Lifecycle](#lifecycle--and-knowing-why-the-server-stopped) — and its transitions carry
  `NetworkChanged`, so a subscriber can tell a rebind from a shutdown.
- **Every start and restart this package drives is retried, and the give-up is an `Error`.** A
  connectivity rebind, an Apple resume restart and a foreground start all run through the same
  bounded backoff — `RestartAttempts` (3), `RestartRetryDelay` (5s, doubling), `MaxRestartRetryDelay`
  (30s) — because the moment they run is the moment a bind is refused: the new interface is not
  routable yet, or the old port is still in `TIME_WAIT`. Do not "fix" a rebind failure by wrapping
  these calls in your own retry; set the options instead. This is *outside* the core's
  `StartRetryAttempts`, so the two multiply — the core never retries a start the caller asked for,
  and on this path the caller is a lifecycle callback with nobody to tell.
- **Never downgrade these to `LogWarning` when generating similar code.** A crash reporter's
  `Microsoft.Extensions.Logging` bridge files an event at `Error` and only a breadcrumb at `Warning`,
  so a server that stopped and logged a warning is a server that told nobody. The same rule applies
  to any handler an app writes on `StateTransitioned`.
- **On Android, a foreground service that never started is now reported.** `StartService` posts an
  intent and returns; Android refuses it inside the service when the app is not entitled to one, and
  the package checks five seconds later and logs at `Error` naming the manifest entries to look at.
  If the OS stops the service while the server is still listening, that is logged at `Error` too. If
  an app generates its own foreground service, do the same — do not assume `StartService` worked.

### Discovery — the other half of hosting on a phone

Binding a port solves being reachable. It does not solve being *found*: the address is assigned by
whatever network the device joined and changes when it moves. That leaves a QR code, typing an IP, or
mDNS.

```csharp
// the device that serves — inside AddShinyHttpServer / on the builder
http.AddHttpServerAdvertisement(o =>
{
    o.ServiceType = "_myapp._tcp";        // a private type: browsing _http._tcp finds printers too
    o.TxtRecords["role"] = "controller";
});

// the device that looks
builder.Services.AddHttpServerLocator();

var found = await locator.FindFirstAsync("_myapp._tcp", TimeSpan.FromSeconds(5));
using var client = new HttpClient { BaseAddress = found!.BaseAddress };

// or bind a list to it
await foreach (var change in locator.WatchAsync("_myapp._tcp", ct)) { … }
```

The advertisement follows the server — published when it starts, withdrawn (with a goodbye packet)
when it stops, re-announced when the device changes network. Read the final instance name back from
`IHttpServerAdvertiser.Publication`: the responder renames a service whose name is already taken.

It follows the **reason** as well as the state, which is the pattern to copy for any handler that
tears something down on `Stopped`: it subscribes to `StateTransitioned`, and a `Stopped` carrying
`Restarting` or `NetworkChanged` leaves the record standing rather than sending a goodbye and a fresh
announcement for a service that never actually went away. If the start half never lands, the
`Stopped` that follows carries `BindFailed`, and that one withdraws.

Publishing is retried — `PublishAttempts` (3), `PublishRetryDelay` (1s, doubling),
`MaxPublishRetryDelay` (15s) — with an `Error` carrying the exception when the attempts are spent. A
responder is least likely to answer at exactly the moment this runs, and the failure it leaves behind
is the hard kind: a server running perfectly, findable by nobody, looking healthy from inside the app.

### TLS on a device

Prefer plain HTTP on the LAN; terminate TLS at a tunnel. A self-signed certificate needs per-device
trust (`ServerCertificate.Create()` / `CreateOrLoad(path)`, plus
`CertificatePinning.CreateHandler(cert)` for the app's own `HttpClient`).

## Testing endpoints

```csharp
await using var app = TestHttpServer.Create(
    server => server.MapMyAppEndpoints(),
    builder => builder.Services.AddSingleton<IClock>(new FrozenClock(…))
);

Assert.Equal("pong", await app.Client.GetStringAsync("/ping"));
```

No port, no listener, nothing to leak when a test fails half way through — but the real parser,
router, middleware and response framing, because only the socket is replaced. `app.CreateClient()`
gives a second caller with its own connection and cookies; `TestHttpServer.Create(..., useHttp2:
true)` speaks HTTP/2 by prior knowledge. `server.CreateInMemoryClient()` does the same for a server
built elsewhere.

Use a **real socket** instead when the thing under test is the socket: TLS, connection limits, or
anything about how the OS frames bytes.

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
| SWS030 | Invalid API version in `[ApiVersion]` / `[MapToApiVersion]` |
| SWS031 | **Warning** — `[MapToApiVersion]` names a version the class never declares |
| SWB001–SWB006 | Switchboard can't be dispatched: generic/inaccessible class, no constructor, duplicate method name, unbindable parameter, unsupported return, generic method |
| SWB007 | **Warning** — `[AllowAnonymous]` on a method of an `[Authorize]` switchboard has no effect |
| SWB008 | `TClient` interface member that can't be called on a client (non-method, non-`Task` return) |
| SWB009 | **Warning** — a contract interface method clients can't call under its name |
| SWB010 | **Warning** — a type `[ExportContract]` can't include |
| SWBC001–SWBC003 | Typed client (`[SwitchboardClient<,>]`): class not partial/top-level, unsupported member, name collision |

The generator also emits metadata for `[RequestTimeout]`, `[DisableRequestTimeout]`, `[OutputCache]`,
`[NoOutputCache]`, `[ValidateAntiforgery]`, `[DisableAntiforgery]`, `[Idempotent]`, `[DisableIdempotency]`,
`[ContentDigest]`, `[DisableContentDigest]`, `[RequireWebhookSignature]` and `[ApiVersion]`/`[MapToApiVersion]`/
`[ApiVersionNeutral]`, exactly as it does for
`[Authorize]`, `[EnableCors]`, `[EnableRateLimiting]` and `[RequireIpFilter]` — a method's attribute
replaces the class's, and a `Disable` anywhere wins.

## Best Practices

1. **Prefer tier 3** for anything with typed parameters; tier 1 for small servers.
2. **Always declare a `JsonSerializerContext`** and never use the reflection JSON overloads.
3. **Route constraints are a closed set** — `byte`, `short`, `int`, `long`, `float`, `double`,
   `decimal`, `bool`, `guid`, `alpha`, `datetime`, `dateonly`, `timeonly`, `timespan`,
   `minlength(n)`, `maxlength(n)`, `length(n)`, `min(n)`, `max(n)`, `range(a,b)`, `apiVersion`. There is **no
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
10. **A tunnel is the public internet.** Authentication and rate limiting first, always — and turn
    off `ContinueIncomingTrace` and health-check details there too.
11. **On a device, assume the address changes.** Bind `IPAddress.Any`, or turn on rebinding; publish
    the server over mDNS rather than asking anyone to type an IP.
12. **A request timeout is cancellation, not a kill.** Pass `ctx.RequestAborted` into the slow work
    or the timeout only changes what the client sees.
13. **Never cache a response for an authenticated caller** without adding the identity to the key.
14. **Prefer the in-memory harness for endpoint tests**, and a real socket for anything about the
    socket.
15. **Subscribe to `StateTransitioned`, not `StateChanged`, when the app has to report why the server
    stopped.** `StateChanged` cannot tell "the user switched it off" from "the listener died", and on
    a device that distinction is the whole bug report. Treat `Reason == Restarting` as *not* down.

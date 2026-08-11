# Shiny.Net.HttpServer — Plan & Status

> Living document. Updated as work lands. Last updated: 2026-08-10.

## Why this exists

ASP.NET Core is heavyweight and does not run on .NET MAUI or in several embedded server
scenarios. This is a dependency-light, fully AOT/trim-clean HTTP/1.1 server that runs anywhere
.NET runs — plus an ngrok-style tunnel so a server embedded in a phone app is reachable from
the public internet.

## Locked decisions

These were chosen up front and are not open questions:

| Area | Decision |
| --- | --- |
| **HTTP core** | Raw sockets + `System.IO.Pipelines`. Own HTTP/1.1 and HTTP/2 stacks, own HPACK. No ASP.NET deps. The transport/protocol split is what let HTTP/2, WebSockets and the tunnel all slot in behind the same `IConnection`. |
| **Protocol selection** | Never guessed. ALPN over TLS, connection preface over cleartext, anything else is HTTP/1.1. |
| **OpenAPI** | Generated from the same compile-time metadata that writes the binders, and from the app's `JsonSerializerContext` for schemas. No reflection, no separate document model. |
| **Security** | Authentication and authorization split, ASP.NET-shaped. Policies are configured at registration; `[Authorize]` is endpoint metadata enforced after routing. JWT is implemented on in-box crypto rather than taking a `Microsoft.IdentityModel` dependency. |
| **Lifecycle** | Start/stop/restart are runtime operations, serialized and idempotent, with an observable state — an embedded server gets toggled, not just booted. |
| **Endpoint model** | Attributes on plain classes — `[Get("/users/{id}")]` on methods. Generator emits route registration and parameter binding. Constructor injection. Zero reflection. |
| **Tunnel** | Own relay protocol as the reference implementation, behind a pluggable `ITunnelProvider` so a Cloudflare/ngrok adapter can be added on desktop/server later. |
| **Scope of first pass** | Full vertical slice: core + generator + tunnel. |
| **Dependencies** | Only `Microsoft.Extensions.*` abstractions. Everything else — JSON, crypto, JWT, OpenAPI, HPACK — is built on what is in the box. |
| **Packaging** | Three assemblies: core, the generator, and JWT. Anything that would be a thin package sitting on core's internals lives in core instead. |
| **HttpContext shape** | Deliberately ASP.NET-shaped: headers, raw body stream, cookies, `Connection.RemoteIpAddress`, raw response writes. |
| **DI** | A real `IServiceScope` per request/response exchange, exactly like ASP.NET Core. Disposed at request end, including `IAsyncDisposable`. |

## The four tiers

The whole point is a gentle ramp: trivial to start, strongly typed when you want it. Each tier is
built on the one below and they compose in the same app.

**Tier 0 — one delegate, no routing.**

```csharp
var server = new HttpServer(new HttpServerOptions { Port = 8080 });
server.OnRequest(ctx => ctx.Response.WriteAsync("hello"));
await server.RunAsync();
```

**Tier 1 — raw routing.** `OnGet`/`OnPost`/… (or `MapGet`/`MapPost`/…, the ASP.NET spelling).

```csharp
server.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong"));
server.OnGet("/users/{id}", ctx => ctx.Response.WriteAsync(ctx.Request.RouteValues["id"]!));
```

**Tier 2 — middleware.** Same shape as ASP.NET Core middleware, as a lambda or as a class.

```csharp
server.Use(async (ctx, next) => { var sw = Stopwatch.StartNew(); await next(ctx); log(sw.Elapsed); });

public sealed class ApiKeyMiddleware(IKeyStore keys) : IHttpMiddleware
{
    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next) { ... }
}

server.Use<ApiKeyMiddleware>();     // resolved per request, from the request's own scope
server.Use(new ApiKeyMiddleware(store));   // or an instance you already have
```

**Tier 3 — source-generated typed endpoints.**

```csharp
[Route("/api/users")]
public class UserEndpoints(IUserService users, ILogger<UserEndpoints> logger)
{
    [Get("/{id:int}")]
    public async Task<IActionResult> GetUser(int id, CancellationToken ct)
        => await users.FindAsync(id, ct) is { } u ? new OkObjectResult(u) : new NotFoundResult();
}

// generator emits, per assembly:
app.MapUserEndpoints();      // one class
app.MapMyAppEndpoints();     // every [Route] class in the assembly
```

### DI is always available, never mandatory

```csharp
// No container. RequestServices resolves nothing; everything else works.
var server = new HttpServer(new HttpServerOptions { Port = 8080 });

// With a container. Scoped services behave exactly as in ASP.NET Core.
var builder = HttpServer.CreateBuilder();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
var app = builder.Build();
```

Endpoint classes are resolved from the per-request scope when registered, and constructed
directly from it when not, so a `Scoped` dependency is one instance shared by everything handling
that request — same contract as ASP.NET Core. `ctx.RequestServices` is the accessor at every tier.

---

## Status — feature complete

Everything in the original plan has landed, plus middleware-as-a-type, OpenAPI, security, runtime
lifecycle control, HTTP/2, WebSockets, SSE, streaming uploads and downloads, runtime-mutable routes,
CORS, rate limiting, IP filtering, and a global exception-handler chain. The solution builds with
zero warnings, **674 tests pass**, and the sample publishes to a 5.3 MB Native AOT binary that
serves HTTP/1.1 and HTTP/2 locally, through a tunnel, and behind JWT auth.

### ✅ Core HTTP — `Shiny.Net.HttpServer`

`Core/` (context, request, response, headers, query, cookies, route values, options, limits, TLS),
`Certificates/` (self-signed generation, client-side pinning),
`Sockets/` (`IConnection`, `SocketConnectionListener` with optional TLS/SNI, `DuplexPipeConnection`
for in-memory transports), `Http1/` (parser, output producer, content-length + chunked body
streams), `Routing/` (template parser, closed-set constraints, prefix-trie router, routing
middleware), `Results/`, `Hosting/` (`HttpServer`, `HttpServerBuilder`, `AddHttpServer()` +
`IHostedService`), `Internal/UrlDecoder`.

Contexts are pooled per connection and reset between requests. Literals beat parameters,
constrained parameters beat unconstrained, catch-all is last resort. 405 is distinguished from 404
and carries `Allow`. HEAD falls back to GET. Duplicate registrations throw at startup.

### ✅ Results — two shapes, one behaviour

`Results.Ok()/NotFound()/…` and the MVC-shaped `IActionResult` types (`OkObjectResult`,
`NotFoundResult`, `CreatedResult`, `ContentResult`, `FileStreamResult`, …) are the same objects
under two spellings, so both styles mix in one app.

JSON has two paths and both are reflection-free: pass a `JsonTypeInfo<T>` directly, or let
`JsonTypeInfoRegistry` find it in a registered `JsonSerializerContext`. Metadata is returned from
the owning context rather than rebuilt against fresh options, so `Results.Ok(widget)` and
`Results.Ok(widget, MyJson.Default.Widget)` produce byte-identical output. The reflection-based
overload still exists but is `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`, so an AOT build
has to opt into it deliberately.

### ✅ Tier 3 — `Shiny.Net.HttpServer.SourceGenerators`

Incremental generator over `[Route]` classes. Emits route registration extension methods, a
zero-reflection binder per parameter, an explicit constructor call per endpoint class, and a
module initializer registering the app's `JsonSerializerContext`s.

Binding is by convention with attributes as the escape hatch: a name matching a route token binds
from the route, anything `IParsable` binds from the query, a complex type on a body-carrying verb
binds from JSON, everything else comes from the container. `[FromRoute]`, `[FromQuery]`,
`[FromHeader]`, `[FromBody]`, `[FromServices]` override that. `HttpContext`, `HttpRequest`,
`HttpResponse` and `CancellationToken` are handed over directly. Bind failures are 400s with a
message naming the parameter and the type it could not become.

Diagnostics: SWS001 invalid template, SWS002 unbindable parameter, SWS003 unsupported return type,
SWS004 unreachable endpoint, SWS005 duplicate route, SWS006 missing JSON metadata, SWS007 multiple
body parameters, SWS008 `[FromRoute]` with no matching token, SWS009 unbound route token.

> **Note on the JSON context.** The generator cannot emit its own `JsonSerializerContext`:
> generators never see each other's output, so `System.Text.Json`'s generator would never see it.
> Instead the app declares the context and this generator registers it — and warns (SWS006) about
> any endpoint type the context does not cover, turning a runtime failure into a build warning.

### ✅ HTTP/2 — `Http2/`

A full server-side HTTP/2 stack written on the same primitives as everything else: frames,
streams, flow control, and HPACK with its own Huffman codec. No `Microsoft.AspNetCore` anywhere,
and it survives AOT like the rest.

- **HPACK** — static and dynamic tables, the RFC 7541 Huffman code (validated against the RFC's own
  test vectors and by Kraft equality over the code lengths), and the variable-length integer coding.
  The decoder keeps a dynamic table because the peer's indices depend on it; the encoder deliberately
  keeps none, since an encoder table that drifts from the peer's produces headers that decode to
  something else entirely.
- **Frames and streams** — DATA, HEADERS, CONTINUATION, SETTINGS, PING, WINDOW_UPDATE, RST_STREAM,
  GOAWAY, PRIORITY. One read loop owns the socket and fans frames out; each stream runs the pipeline
  concurrently, and all writes funnel through a serializing frame writer.
- **Flow control** — per stream and per connection, in both directions, with windows topped up in
  chunks rather than per frame.
- **Selection** — ALPN (`h2`) over TLS, connection preface over cleartext. The cleartext sniff
  decides the moment the bytes diverge from the preface, not once 24 bytes could have arrived —
  otherwise every HTTP/1.1 request shorter than the preface hangs.

Everything above the transport is untouched: the same routes, binders, middleware, authorization and
handlers serve both protocols, because `Http2RequestMapper` turns pseudo-headers back into the
`HttpRequest` the rest of the server already understands.

Verified against `HttpClient` with `RequestVersionExact` — 512 KB bodies through flow control, 20
multiplexed streams on one connection, and HTTP/1.1 clients served on the same port.

**Not implemented**: server push (deprecated and removed from browsers), trailers, and the
`h2c` `Upgrade:` handshake (prior knowledge only, which is what every h2c client actually uses).

### ✅ WebSockets — `WebSockets/`

RFC 6455 over the same connection abstraction: handshake, frame codec with unmasking, fragment
reassembly, automatic ping/pong, and the close handshake. Sub-protocol negotiation picks by the
server's preference order.

The upgrade needed a real seam in the HTTP/1.1 output producer — a 101 is a protocol handover, not a
bodyless response — and it surfaced a genuine bug: `StartAsync` staged the response head without
flushing it, so an upgrade deadlocked with each side waiting for the other. `StartAsync` now flushes;
the body path uses an internal non-flushing variant so ordinary responses still take one write.

Tested against `ClientWebSocket` rather than a matching hand-rolled client, because a framing bug
only a sympathetic client tolerates is exactly the bug worth catching.

### ✅ Server-Sent Events — `Sse/`

`ctx.SendEventsAsync(...)`, an `IAsyncEnumerable<ServerSentEvent>` overload, and
`Results.ServerSentEvents(...)`. Correct multi-line `data:` framing, `Last-Event-ID` for resumption,
heartbeats, and a flush after every event — a buffered event stream is a broken one.

### ✅ Uploads and downloads — `Files/`

**Uploads**: a streaming `multipart/form-data` parser where each part's body is a stream that ends at
the boundary, so a 2 GB upload goes straight to disk. `ReadFormAsync` buffers instead, bounded on
purpose. `Content-Disposition` parsing handles the RFC 5987 `filename*` form, and `SafeFileName`
strips directory components — a client-supplied filename joined onto an upload directory is the
classic traversal hole.

**Downloads**: `FileDownloadResult` with byte ranges (including suffix ranges), 416 for a range past
the end, `ETag`/`Last-Modified` conditional GETs, `If-Range` so a stale resume does not splice two
files together, and a content-type table that falls back to `application/octet-stream` rather than
guessing `text/html`.

### ✅ Minimal endpoints and modules — `Endpoints/`

Two ways to bolt endpoints on, alongside `[Route]` controllers:

- **`IHttpEndpoint`** — verb on the class, one `Handle`/`HandleAsync` method. The same generator, the
  same binding, the same diagnostics; just the grouping removed. Picked up by the same
  `MapMyAppEndpoints()` call as controllers.
- **`IEndpointModule`** — a set of routes registered at runtime, tagged with the module so
  `UnmapModule<T>()` takes them all away again. Plus `MapGroup` for a shared prefix and a
  `RouteEndpointBuilder` for attaching authorization and OpenAPI metadata inline.

### ✅ Runtime-mutable routes — `Routing/`

The route table is an immutable trie behind a volatile field. Adding or removing a route builds a
new table and publishes it with one write, so a request in flight sees the whole change or none of
it, and matching never takes a lock. `MapRoute`/`Unmap`/`UnmapAll`/`ClearRoutes` work while the
server is running. Middleware stays frozen — that pipeline is composed once.

### ✅ Global exception handling — `Diagnostics/`

`IExceptionHandler`, several per registration, tried in order until one claims the exception; the
connection's own 500 is the last resort rather than the first response. A handler that throws is
treated as a decline, because replacing the original exception hides the thing worth seeing.

`AddProblemDetails()` registers the catch-all at the end of that chain: it maps the exception to a
status code — conservatively, so only unambiguous client mistakes become 4xx — and writes an RFC
9457 body. A 4xx carries the exception message, a 5xx never does, and the full type/stack is behind
`IncludeExceptionDetails` for development only. Because routing's 404 and authentication's 401 never
throw, `UseProblemDetails()` adds a middleware that gives *bodiless* error responses the same shape;
a handler that wrote its own body is left alone. This is also why the terminal 404 no longer flushes
its own headers — starting the response there made it the one status no middleware could act on.

### ✅ Problem details — `Diagnostics/ProblemDetails*.cs`

RFC 9457, with `Results.Problem` and `Results.ValidationProblem` alongside the automatic path. The
writer is hand-rolled on `Utf8JsonWriter` rather than `JsonSerializer`, because the extension bag is
`object?` and reflecting over it is exactly what this server does not do — the supported value
shapes are a closed set and anything else falls back to its string form, so an error body can never
be the thing that breaks an AOT build or throws while reporting a failure.

### ✅ Static files — `StaticFiles/`

`IStaticFileSource` rather than a directory path, because the interesting case on a phone is not a
directory: `EmbeddedFileSource` serves web assets out of the assembly, which is the only option in a
MAUI or single-file build, and `PhysicalFileSource` serves a folder. `CompositeFileSource` puts a
directory in front of the embedded copy so an edit shows up without a rebuild.

Ranges, ETags, `If-None-Match` and 304 come free — the middleware resolves a path and hands over to
`FileDownloadResult`, which already had all of it. What is left is the part that has to be right:
paths arrive already percent-decoded, so `%2e%2e%2f` is a plain `../` by the time anything sees it,
and containment is checked after normalization *and* after resolving links. Dotfiles and unknown
extensions are refused by default — guessing a type for an unknown extension is how a content
directory starts serving HTML. `FallbackFile` handles single-page apps, but only for requests that
look like navigations, so a missing script still 404s honestly.

### ✅ Response compression — `Compression/`

Brotli, gzip and deflate, negotiated from `Accept-Encoding` with q-values (`q=0` is a refusal; a
bare `*` takes the server's preference). Worth more here than in a datacentre: an embedded server is
reached over cellular or a tunnel, where the CPU is idle and the bytes are not.

The decision is made on the first byte written, not when the middleware runs — nothing has set a
content type yet at that point. It works by wrapping `IResponseBodyControl`, so every write path
funnels through one place and a handler using `Body`, `BodyWriter` or any convenience helper is
covered without knowing this exists. Compressing clears the declared length, since the compressed
size is unknown until the last block. Ranges, pre-encoded bodies, 204/304, HEAD and already-
compressed media types are all passed through untouched, and `Vary: Accept-Encoding` is appended
from an `OnStarting` callback either way — set before the handler runs, its own `Vary` would
overwrite it.

### ✅ Security — `Security/` and `Shiny.Net.HttpServer.Jwt`

Authentication and authorization are separate, and the split is load-bearing: a handler answers
"who is this?" and never "may they?", so an endpoint can be public on a server that still knows who
is calling it.

```csharp
builder.Services
    .AddAuthentication()
    .AddJwtBearer(o => { o.Issuer = "shiny"; o.Audience = "app"; o.SigningKey = key; });

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("admin", p => p.RequireRole("admin"));
    o.AddPolicy("self", p => p.RequireAssertion(ctx =>
        ctx.User.FindFirst("sub")?.Value == ctx.HttpContext.Request.RouteValues["id"]));
    // o.SetFallbackPolicy(p => p.RequireAuthenticatedUser());   // deny by default
});

app.UseAuthentication();    // before routing — identity does not depend on the endpoint
app.UseAuthorization();     // after routing — what is required is metadata on the endpoint
```

`[Authorize]` and `[AllowAnonymous]` go on endpoint classes and methods; `RequireAuthorization()`
and `AllowAnonymous()` do the same for raw routes. A method's `[Authorize]` adds to its class's
rather than replacing it, so narrowing is additive and cannot accidentally widen; `[AllowAnonymous]`
always wins, including over a fallback policy.

**401 versus 403 is enforced, not approximated.** An anonymous caller gets 401 with a
`WWW-Authenticate` challenge naming the scheme and, for a bad token, the reason. An authenticated
caller who still is not allowed gets 403 — another login will not help them. The reason for a
rejection goes in the challenge and the log, never the body.

**JWT** (`Shiny.Net.HttpServer.Jwt`) is written on `System.Security.Cryptography` and
`Utf8JsonWriter`/`Utf8JsonReader` — no `Microsoft.IdentityModel`, no package dependency, nothing for
the trimmer to chase. HS256/384/512, RS256/384/512 and ES256/384/512, for both creating and
validating. `AddJwtBearer` registers a `JwtTokenGenerator` built from the same configuration that
validates, so a login endpoint cannot issue tokens the server will then reject.

The two classic JWT holes are closed deliberately and covered by tests: `alg: none` never reaches a
key, and the algorithm comes from the configured key rather than the token's own header, so an
RS256 server cannot be talked into verifying an HS256 token. Signature comparison is fixed-time,
ECDSA signatures use the JWS raw `r||s` form rather than DER, and validation fails closed — an empty
issuer allow-list rejects everything rather than accepting anything.

### ✅ TLS and endpoints — `Core/HttpServerOptions`, `Certificates/`

**Several endpoints at once.** `Options.Endpoints` holds any number of address/port/TLS
combinations, each bound by its own listener and accept loop. TLS is per endpoint rather than per
server because that is how it is actually used — cleartext to the device, TLS to the network:

```csharp
options.Listen(IPAddress.Loopback, 5000);
options.ListenHttps(IPAddress.Any, 5001, certificate);
```

`Address`/`Port`/`Https` remain the single-endpoint shorthand and are what most embedded servers
want; the list takes over entirely once it has anything in it, so adding one endpoint never
silently means two. `ListenUrls` reports them all, `ListenUrl` the first. A partial bind failure
unwinds every endpoint that did bind rather than leaving the server half listening, and
`RestartAsync` re-reads the list.

**The handshake moved off the accept loop.** TLS was previously negotiated inside `AcceptAsync`,
which meant one client that connected and then said nothing stalled every other connection to the
server. Connections are now returned unhandshaken and completed via `IConnectionInitializer` on
their own task, under `HttpsOptions.HandshakeTimeout` (10s). `ClientCertificateMode.Require` is
also now enforced — `SslStream` asks for a certificate but does not insist on one, so `Allow` and
`Require` behaved identically.

**Certificates — `ServerCertificate` and `CertificatePinning`.** A public CA cannot issue for
`192.168.1.42` or for a phone that changes networks, so an embedded server has to sign its own.
`ServerCertificate.Create()` does it in managed `System.Security.Cryptography` — no OpenSSL, no
platform tooling, runs on iOS and Android unchanged — and encodes the rules that get certificates
rejected on a device rather than here: subject alternative names covering localhost, the host name
and every local address (CN has been ignored for years), `serverAuth` EKU, a 397-day lifetime under
Apple's 398-day ceiling, RSA-2048 or P-256, backdated an hour for clock skew, and a PKCS#12
round-trip because on Apple platforms the key on a freshly created certificate is not in a form
`SslStream` will accept as a server credential. `CreateOrLoad(path)` persists it so a pinned client
survives a restart, renewing 30 days out.

Trust is the part that does not have one answer, and the split is worth stating plainly:

- **The app's own `HttpClient`** — nothing to install. `CertificatePinning.CreateHandler(cert)`
  replaces chain validation with an SPKI pin, which is stricter than the public PKI, not weaker.
- **A browser or WebView** — the certificate must be installed and trusted per device. On iOS that
  is a profile install *plus* a separate switch under Settings › General › About › Certificate
  Trust Settings; on Android 7+ a user CA is trusted by Chrome but not by apps without a
  `network_security_config`. Nothing in this library avoids that ceremony.
- **Anything internet-facing** — don't. Terminate TLS at the tunnel or relay with a real
  certificate and let the server speak cleartext behind it, which the tunneling support already
  does.

For a device-local server, plain HTTP on loopback plus `NSAllowsLocalNetworking` or an Android
`network_security_config` entry stays the pragmatic default. TLS earns its keep on the
network-facing endpoint.

### ✅ CORS, rate limiting and IP filtering — `Cors/`, `RateLimiting/`, `Security/`

Three middleware modules, same shape as each other: a policy type with a builder, named policies in
options, a default that applies to everything, and per-endpoint overrides as route metadata.

```csharp
app.UseIpFilter(p => p.AllowLoopback().AllowPrivateNetworks());
app.UseRateLimiter(new FixedWindowRateLimitPolicy(300, TimeSpan.FromMinutes(1)));
app.UseCors(p => p.WithOrigins("https://app.example.com").AllowAnyHeader().AllowAnyMethod());
```

A container is optional for all three: pass the policy inline, or register named ones with
`AddCors`/`AddRateLimiter`/`AddIpFilter` and name them per route (`.RequireCors("public")`,
`.RequireRateLimiting("uploads")`, `.RequireIpFilter("admin")`, and the matching
`DisableCors`/`DisableRateLimiting`/`AllowAnyIp` opt-outs). On generated endpoints the same thing is
an attribute — `[EnableCors]`, `[EnableRateLimiting]`, `[RequireIpFilter]` and their `Disable`
counterparts — emitted as metadata by the generator, so nothing is discovered at runtime.

**All three run before routing**, which is the decision the rest follows from. A limiter that only
covered mapped routes would let a scanner's 404s through at full price; a whitelist that did would
answer the whole internet's 404s; and a CORS preflight is an `OPTIONS` to a path that only answers
`GET`, so the router would 405 it and the real request would never be sent. Per-endpoint policies
still work, because each middleware asks the router which endpoint the request *would* reach — for a
preflight, the one its `Access-Control-Request-Method` names.

- **CORS** — origins by list or predicate, methods, headers, exposed headers, credentials, preflight
  max-age. Preflights are answered here and never forwarded. The wildcard is only emitted when the
  policy really does not care who is asking: with credentials, or with named origins, the actual
  origin is echoed and `Vary: Origin` is appended (appended, not set — a handler's own `Vary` is not
  something to trample). Response headers go on at `OnStarting` for the same reason. A policy that
  asks for `AllowAnyOrigin()` *and* `AllowCredentials()` throws when it is built rather than
  producing headers no browser will accept.
- **Rate limiting** — fixed window, sliding window, token bucket and concurrency, all partitioned by
  a selector (`ByIpAddress` by default, plus `ByHeader`, `ByUser`, `Global`) and all on `TimeProvider`
  so their tests do not sleep. The lease is held for the whole request and released when it
  completes, which is what makes the concurrency limiter mean anything. Idle partitions are swept —
  a limiter partitioned by IP and left alone grows an entry per address that ever knocked — but never
  while a partition holds permits or a live window, so nobody resets their own allowance by pausing.
  Rejections carry `Retry-After` rounded *up*; rounding down invites the retry storm the header
  exists to prevent.
- **IP filtering** — allow and deny lists of CIDR ranges, with `IpAddressRange` masking host bits
  itself rather than rejecting `10.0.0.5/8` the way `System.Net.IPNetwork` does, and unmapping
  IPv4-mapped IPv6 on both sides so a dual-stack listener does not break every IPv4 rule. Deny beats
  allow; one allow entry turns a blacklist into a whitelist; an unknown remote address fails closed.
  The address checked is `Connection.RemoteIpAddress`, so trusting `X-Forwarded-For` remains the
  server-level opt-in it already was — a filter that read it by default could be walked past with one
  header.

### ✅ Lifecycle — start, stop, restart at runtime

`StartAsync`, `StopAsync` and `RestartAsync` are serialized against each other and idempotent, so a
UI toggle can call them without tracking state. `State` and `StateChanged` expose the transitions
(`Starting` → `Running` → `Stopping` → `Stopped`), a failed bind returns to `Stopped` rather than
sticking in `Starting`, and `RestartAsync` picks up a changed port or newly configured TLS.
`AddHttpServer(..., autoStart: false)` registers and configures the server without listening, which
is what an app that starts serving on a button press wants.

Two real bugs were fixed here, both now covered:

- The shutdown token was created once and cancelled permanently, so a restarted server bound a port
  and then silently never answered on it.
- Unbinding while the accept loop sat between iterations threw instead of stopping cleanly, because
  the listener could not tell "never bound" from "bound and since unbound".

### ✅ OpenAPI — `OpenApi/`

`app.MapOpenApi()` serves an OpenAPI 3.0.3 document at `/openapi.json`;
`OpenApiDocumentBuilder.BuildJson(app)` returns the same document as a string, for writing it out at
build or test time instead.

The document is assembled from two sources, both compile-time:

- **The route table.** Generated endpoints carry an `ApiOperation` the generator emitted from the
  analysis it had already done to write the binder — parameter names and sources, the body type, the
  return type, and the `<summary>` from the method's doc comment. Raw routes get path parameters
  inferred from their template (including the constraint's type), plus whatever `Describe()` adds.
- **The app's `JsonSerializerContext`.** Schemas are read off `JsonTypeInfo` rather than
  `Type.GetProperties()`, so property names go through the same naming policy the serializer uses and
  the document cannot describe a payload the server would not actually produce. It also means schema
  generation is reflection-free and survives AOT.

`[Produces(200, typeof(Widget))]` declares responses a method returning `IActionResult` has
deliberately hidden. `[ApiTags]` groups operations, `[ApiExclude]` hides an endpoint from the
document without unmapping it, and `OpenApiOptions.ConfigureOperation` applies conventions across
every endpoint.

A trailing optional route parameter becomes two paths, because OpenAPI has no way to say a path
segment is optional and the route genuinely matches two URLs.

Written with `Utf8JsonWriter` straight to bytes: no document object model to keep in sync with the
spec, and nothing to register for serializing it.

### ✅ Tunnel — `Tunneling/` and `Tunneling/Relay/`

Both ends live in core. They were separate packages at first, but at ~1,500 lines together — and
with the relay already reaching into core's internals for its listener — the package boundary was
buying nothing and costing an assembly split. One namespace, `Shiny.Net.HttpServer.Tunneling`,
covers the client and the relay.

`ITunnelProvider : IConnectionListener` in core, plus a 9-byte frame protocol
(type / stream id / length) and `TunnelChannel`, the shared framing pump both ends use.

The client dials out over TCP (+ optional TLS), registers with a token and a requested subdomain,
and unpacks each inbound stream into a `DuplexPipeConnection` handed to `HttpServer.ServeAsync`.
Outbound-only, so it works from a cell network behind CGNAT. Keepalive pings and automatic
reconnect with a configurable delay.

The relay runs two listeners: a control port where clients register, and a public port where the
world arrives. It reads **every** request head — not just the first — to route by Host, and reads
enough framing (Content-Length, chunked) to know where each body ends. That is what makes keep-alive
safe: a connection is pinned to the tunnel its first request named, and a later request for a
different host gets a 421 rather than being delivered to another tenant. Forwarded headers
(`X-Forwarded-For`/`-Proto`/`-Host`) are injected, and the tunnelled server still has to opt into
trusting them.

### ✅ Azure Relay — `Shiny.Net.HttpServer.AzureRelay`

A public HTTPS endpoint with no infrastructure to run. The device dials out to Azure Relay and holds
a hybrid connection open; Azure owns the address and forwards down it.

Two modes. `Http` (default) gives a URL any client can hit — the relay hands over a parsed request,
so the provider synthesises HTTP/1.1 onto a `DuplexPipeConnection`, serves it through the whole
normal pipeline, and reads the response back off the wire with a small response parser. Every result
type and every piece of middleware works unchanged, at the cost of buffering the response, which
rules out SSE and WebSockets. `RelayedStream` wraps `HybridConnectionStream` as an `IConnection`
instead: full fidelity, but callers must speak Azure Relay rather than plain HTTP.

`AzureRelaySas` mints scoped, short-lived tokens on a backend so a device never holds the key —
paired with `RefreshSharedAccessSignature`, which is consulted on every reconnect. The package
deliberately does not claim AOT compatibility: `Microsoft.Azure.Relay` drags in Azure.Identity, MSAL
and IdentityModel, and quarantining that weight is exactly why it is a separate package.

### ✅ SSH — `Shiny.Net.HttpServer.Ssh`

`ssh -R`, in library form, over SSH.NET. Works against anything you can log in to: a VPS you own
(stable hostname, your TLS, `permitlisten` restricting the key to one port) or a hosted tunnel
(sish, localhost.run, Serveo).

The provider owns a private ephemeral loopback listener and points the remote forward at it, so the
app binds no port of its own and the whole thing still plugs into `RunTunnelAsync` like any other
`ITunnelProvider`. Accepted connections are wrapped so they report `IsTunneled` — and forward
`IConnectionInitializer`, without which the socket's pipes are never opened.

Host keys are *checked*: SSH.NET trusts any key by default, so connecting without either a pinned
SHA-256 fingerprint or an explicit `AcceptAnyHostKey` fails. `CaptureUrlFromSession` reads the
address hosted tunnels assign, which they print on the session channel and nowhere else. Reconnect
with backoff is on by default, because a phone changing networks kills the tunnel underneath.

### ✅ Content negotiation — `Negotiation/`

`Results.Negotiate(value)` picks a representation from `Accept` — q-values, wildcards, and the
specificity tie-break that stops a browser's trailing `*/*;q=0.8` outranking the `text/html` it
actually asked for. JSON and plain text are registered by default; `AddFormatter` covers the rest.

Answers **406** when the client accepts nothing on offer, rather than sending a format it said it
could not read. `IOutputFormatter` is non-generic because the choice happens at runtime, and the
JSON formatter closes that gap through `JsonTypeInfoRegistry` rather than reflection — so a type
gets a representation because its metadata was registered, not because something reflected over it.
The text formatter deliberately declines objects whose only string form is their type name.

### ✅ Basic authentication — `Security/BasicAuthentication.cs`

RFC 7617, for the case where the shortest path to a password prompt is the right one: every browser
and every HTTP client already speaks it, with no token endpoint and no session to manage.

Two things it does that a naive implementation does not. It **refuses to run over an unencrypted
connection** — Basic sends the password on every request, base64-encoded, which is spelling rather
than encryption, so plain HTTP from a real network is rejected outright while TLS, a tunnel, and
loopback are allowed. And it implements `IAuthenticationChallenge`, because the pipeline's generic
challenge names whatever scheme ran last, and a browser handed anything other than `Basic` shows the
user no prompt and no way in.

Passwords are not kept — only a hash of `username:password`, compared in fixed time with every entry
checked even after a match, so timing says nothing about whether an account exists. A wrong username
and a wrong password produce the same answer for the same reason. A real user database belongs in
`ValidateAsync`, with whatever hashing it already uses.

### ✅ API key authentication — `Security/ApiKeyAuthentication.cs`### ✅ API key authentication — `Security/ApiKeyAuthentication.cs`

The scheme for a device, a script or a webhook — anything with no user to log in. Keys arrive by
header, `Authorization: ApiKey …`, or (opt-in, and documented as a bad idea) a query parameter.

Only SHA-256 hashes are kept, never the keys: a dumped options object hands over nothing usable, and
hashing equalises length, which is what makes `FixedTimeEquals` meaningful. Every entry is checked
even after a match, so response time does not leak a key's position in the list. A key maps to a
named principal with roles, so authorization works on it exactly as on a JWT.

### ✅ Cookie authentication — `Security/CookieAuthentication.cs`

The scheme a browser wants. The ticket is AES-GCM encrypted rather than merely signed — a cookie
lives on a machine you do not control and carries claims — and keyed by a short stable key id, so a
rotation can add a new primary key while cookies issued under the old one keep working until they
expire.

Claims are serialized by hand into a length-prefixed binary format. Not for speed: it makes the
parser total, so a truncated or tampered payload fails as a parse error rather than an exception
from somewhere deeper. `SlidingExpiration` reissues at the halfway mark rather than on every
request, because a `Set-Cookie` on every response breaks shared caching for nothing.

Denial is answered per-caller through `IAuthenticationChallenge`: a browser navigation gets a
redirect to `LoginPath` with the original URL attached, an API client gets its 401. Only the scheme
knows which is useful, which is why the authorization middleware asks rather than deciding.

### ✅ HTTP/3 — `Http3/`

QUIC over UDP on its own listener, because HTTP/3 does not run on TCP at all. Varints (RFC 9000
§16), frames (RFC 9114) and QPACK (RFC 9204) with the full 99-entry static table.

The dynamic table is deliberately refused — the server announces a capacity of zero, which is
spec-legal and removes the entire class of head-of-line bugs that come with encoder streams,
insert-count tracking and blocked request streams. Responses use static references where they exist
and literals otherwise, so the encoder stream stays empty and nothing can ever block waiting for a
table to catch up.

`ListenHttp3Async` starts the endpoint and adds the `Alt-Svc` header to the TCP listener's
responses, since a client has no other way to discover that QUIC is available.

**Verified as far as this machine allows.** QUIC needs msquic, which .NET ships on Windows and Linux
and *not* on macOS, so the codecs are tested exhaustively (RFC 9000's own worked examples, the QPACK
static table against the RFC, round trips, and every malformed-input path) while the live transport
is covered by a test that skips where `QuicListener.IsSupported` is false. The wire protocol has not
been run against a real HTTP/3 client on this machine.

### ✅ Blazor WebAssembly — `StaticFiles/BlazorWebAssemblyExtensions.cs`

`app.UseBlazorWebAssembly("./wwwroot")` serves a published Blazor app, and there is an
`Assembly` overload for the packaged case where the app lives in embedded resources.

Verified against a real `dotnet publish` of a Blazor WASM app — 26 MB of assets, 36 wasm assemblies
— loaded in Chrome, booted, and interactive. Only one thing had actually been missing: `.dat` (the
ICU globalization data) had no content type, and an unknown extension is not served, so the runtime
died on a 404 before it started. `.dat`, `.blat`, `.webcil`, `.dll`, `.pdb` and `.webmanifest` are
now in the map.

Beyond that it arranges the three things a publish needs: the single-page fallback so `/counter`
survives a reload, **precompressed sidecars** (a publish emits `.br`/`.gz` beside every file,
compressed once at maximum effort — serving those beats recompressing 26 MB per request at a level
chosen for speed), and a cache policy that treats fingerprinted `_framework` assets as immutable
while the entry document is `no-cache`.

Sidecar serving is opt-in for ordinary static files (`ServePrecompressedFiles`), because a directory
with an unrelated `.gz` in it should not start serving that as an encoding of something else.

### ✅ Sessions — `Sessions/`

`ISession` injected as a scoped service, so a handler or endpoint class takes one in its constructor
and never reaches for `HttpContext`. `ISessionStore` is pluggable with an in-memory default —
sessions are lost on a restart, which is the honest trade for having no dependency, and anything
that must survive one belongs in a database.

The session id is carried in a cookie protected by the same `TicketProtector` the auth cookie uses:
encrypted, not merely signed, because an id read from a log or a proxy can be replayed. Everything
is lazy — the store is not touched until something is read, and no cookie is issued for a visitor
whose session stayed empty, so serving a static file does not mint a session nobody asked for.

Two details worth knowing. The commit runs in a `finally`, so state written before a handler threw
is still saved — it is state the user already caused. And a *brand-new* session's cookie does not
survive an unhandled exception: the connection resets the response to write its 500, which discards
every header middleware staged. The data is saved but orphaned; an established session is unaffected.

### ✅ Quick tunnels — `Shiny.Net.HttpServer.Ssh`

`AddQuickTunnel()` gives a phone a public HTTPS address with no account, nothing installed and no
infrastructure — SSH to localhost.run (or sish, or Serveo) with presets for each. Pure managed code,
so it runs where an agent-based tunnel cannot, which is the whole reason the ngrok provider was
removed.

Shaped for a UI, because the URL is something a person reads off a screen and gives to a customer:
`QuickTunnel` is `INotifyPropertyChanged` with `PublicUrl`, `State` and `LastError`, so a MAUI view
binds straight to it. That is not decoration — a free tunnel assigns a **different address on every
reconnect**, and a phone reconnects whenever it changes network, so an app that read the URL once
would be displaying a dead link minutes later. `PublicUrl` is cleared the instant the connection
drops rather than left stale, and the events fire on a background thread, which the docs say plainly
because MAUI will not marshal them.

Verified end to end against the local sshd fixture — start, serve a request on the reported URL,
stop, and the notifications a view depends on. Nothing in the suite opens a tunnel to the internet.

### ✅ File browser — `FileBrowser/`

A directory served over HTTP: `GET` it for a JSON listing, `GET` a file for its bytes, `PUT` to
write, `DELETE` to remove. On a phone the root is `FileSystem.AppDataDirectory`, which is the case
it was built for.

Mapped as **routes rather than middleware**, and that is the whole design decision:
`MapFileBrowser` hands back the endpoints it registered, so `RequireAuthorization()` locks the lot
or `RequireAuthorizationForChanges("editors")` leaves reads open and puts a policy on anything that
writes. Middleware could not express that, because authorization is endpoint metadata.

Read-only until told otherwise — `AllowWrite` and `AllowDelete` are both off by default, since the
version of this that cannot fill a device's storage or replace a file something depends on is the
one worth defaulting to. Uploads are bounded by `MaxUploadBytes`, counted as they stream rather than
trusting `Content-Length`, and written to a staging file that is moved into place, so a refused
upload leaves the previous file intact. A directory is only deleted when empty: recursive delete
behind a URL is one mistyped path away from taking everything, and a phone has no undo.

Containment reuses the static file handler's path normalization, checked after decoding and again
after resolving links, with dotfiles hidden and an optional `Filter` that both hides an entry from
listings and refuses it directly.

### ✅ Tests — `tests/Shiny.Net.HttpServer.Tests` (966 tests, xunit v3 on Microsoft.Testing.Platform)

Route templates and constraints, router precedence/405/HEAD/backtracking, the HTTP/1.1 parser
including limits and malformed input, content-length and chunked body streams including draining,
end-to-end HTTP over a real ephemeral socket (keep-alive, pipelining, chunked upload and download,
graceful shutdown, 50 concurrent requests), DI scope lifetime and disposal, the generator's real
output (every binding source, every return shape, 400s, 404s), middleware ordering and request-scope
resolution in both forms, the generated OpenAPI document (paths, parameters, bodies, responses,
component schemas, exclusions, security), JWT round-trips across all nine algorithms plus the
forgery attempts they exist to stop, authorization end to end over HTTP (401 vs 403, policies,
roles, fallback, `[AllowAnonymous]`), the server lifecycle including restart and concurrent
transitions, dynamic route add/remove under concurrent traffic, minimal endpoints and modules,
multipart uploads and range downloads, HPACK against the RFC's own vectors, WebSockets against
`ClientWebSocket`, HTTP/2 against `HttpClient` with `RequestVersionExact`, `TunnelProtocol` framing,
`DuplexPipeConnection`, and a full relay + tunnel + server loop over loopback.

CORS, rate limiting and IP filtering add 84 of those: preflights (approved, refused for origin,
method and header, and for paths that match nothing), the credentialed-wildcard policy that has to
throw when it is built, `Vary` appended rather than replaced, every limiter algorithm against a
hand-driven `TimeProvider` — including partition eviction, and the concurrency permit held across a
real in-flight request and returned after it — CIDR matching including the non-byte-aligned prefixes
that are easy to get silently wrong, deny-beats-allow, failing closed on an unknown address, and all
three modules driven from route metadata, from `Require…`/`Disable…` on a raw route, and from the
attributes the generator emits on typed endpoints.

`RouteTemplateParityTests` pins the two route-template parsers — the runtime one and the
generator's netstandard2.0 copy — to the same grammar, which is the one duplication in the codebase
that would otherwise rot silently.

### ✅ Samples

`samples/Sample.Api` shows all four tiers in one app, with `--tunnel` to also serve through a relay.
`samples/Sample.Relay` hosts the reference relay.

**Verified end to end**: routing, `:int` constraints, catch-all, trailing slash, 404 fallback,
405 + `Allow`, HEAD→GET, keep-alive reuse, chunked request bodies, 50 concurrent requests, SIGINT
graceful shutdown, per-request DI scoping, generated endpoints (route/query/header/body binding and
their 400s), and the same routes served through the tunnel including 300 KB bodies, keep-alive, and
relay restart with automatic reconnect.

**Native AOT verified**: `dotnet publish -c Release -r osx-arm64` produces a 5.3 MB self-contained
binary with zero trim/AOT warnings, serving HTTP/1.1 and HTTP/2, generated endpoints, JSON in both
directions, the OpenAPI document, the tunnel, JWT signing and validation, and an in-app restart.

---

**`Sample.Maui`** — a MAUI app that is the whole point of the library in one screen: it serves an
embedded page, a small JSON API and a file browser over the app's own storage, shows the LAN
address, and publishes to the internet through `QuickTunnel` with no account and nothing installed.
Everything but `/ping` sits behind Basic auth whose username and password are editable on the
screen, backed by an `IBasicCredentialValidator` reading the device keychain — so changing them
takes effect on the next request with nothing restarted. The view model is the part
worth copying — every `MainThread` hop in it exists because the tunnel raises changes from a
background thread, and the view binds to `PublicUrl` rather than reading it once, because a free
tunnel reassigns the address on every reconnect.

Built for Android, iOS and Mac Catalyst, and run on Mac Catalyst: page, `/ping`, `/api/device` (real
`DeviceInfo` and battery) and a `POST` round trip through `/api/notes`, verified in a browser.

Three platform gotchas are encoded in it, each of which fails silently rather than with an error:
Mac Catalyst's sandbox needs `com.apple.security.network.server` to listen at all, iOS 14+ needs
`NSLocalNetworkUsageDescription` or the app is denied local network access without being asked, and
Android needs `INTERNET`.

## Known limitations

Deliberate, and worth knowing before this goes anywhere real.

- **HTTP/2 has no server push, trailers, or `h2c Upgrade:` handshake.** Push is deprecated and gone
  from browsers; prior-knowledge h2c is what real clients use.
- **HTTP/3 has not been run against a real client**, only against its own codec tests — QUIC is
  unavailable on the machine it was written on. It also declines QPACK's dynamic table, and does not
  implement server push or `Extended CONNECT`.
- **The relay pins a public connection to one tunnel.** A client that reuses a connection for a
  different Host gets 421 Misdirected Request. Standard behaviour, and browsers never hit it
  because their connection pools are keyed by authority.
- **The relay does not inspect responses.** It forwards them verbatim, which is correct but means it
  cannot enforce anything on the way out.
- **Rate limit state is per process.** Each server counts its own requests, so a fleet behind a load
  balancer enforces its limit N times over. A distributed limiter needs a shared store, which is a
  dependency this deliberately does not take.
- **CORS is a browser mechanism, not a defence.** The headers tell a browser what script may read.
  `curl` and every non-browser client ignore them, so CORS is never a substitute for authorization.
- **The IP filter reads the transport address**, and only honours `X-Forwarded-For` when the server
  as a whole opted into forwarded headers. Behind a proxy without that opt-in a whitelist sees the
  proxy, not the caller; with it, whoever can reach the port can claim any address they like.
- **WebSockets have no permessage-deflate.** No extensions are negotiated at all.
- **The HPACK encoder does not Huffman-code its output.** Emitting raw literals is always legal and
  costs a few bytes per response; the decoder handles Huffman because every real client sends it.
- **`ReadFormAsync` buffers.** Bounded on purpose — stream with `ReadMultipartAsync` for real files.
- **JWT only, and JWS only.** No JWE (encrypted tokens), no JWKS endpoint or key discovery, no
  refresh-token flow, and no revocation list — a token is valid until it expires.
- **Authentication handlers are tried in registration order** and the first to recognise a request
  wins. There is no per-endpoint scheme selection.
- **A cookie ticket cannot be revoked before it expires** — the claims travel in the cookie rather
  than a server-side session. `ValidateTicketAsync` is the hook for checking anyway.
- **No anti-forgery tokens.** `SameSite=Lax` covers most of CSRF; a form flow that needs `None` has
  to bring its own.
- **Sessions are in-memory by default** and do not survive a restart. The store is an interface, so
  a shared one can be plugged in; none is written.
- **OpenAPI is 3.0.3 only**, and a type only gets a schema if it is in a registered
  `JsonSerializerContext` — the same requirement as returning it from an endpoint, and the generator
  warns (SWS006) when it is not met. A catch-all token is documented as an ordinary path parameter,
  since OpenAPI cannot express a multi-segment one.
- **No OpenAPI UI is bundled.** The document is served; pointing Swagger UI or Scalar at it is up to
  the host, and embedding one would mean shipping a megabyte of someone else's JavaScript.
- **Registration tokens are compared with a fixed-time equality check, but there is no per-tunnel
  rate limiting**, so a relay exposed publicly wants something in front of it.

## Possible next steps

1. Run HTTP/3 against a real client on a platform with msquic, which is the one thing its tests
   cannot do here.
2. **No ngrok or Cloudflare provider, deliberately.** Both tunnel through a binary — `ngrok`,
   `cloudflared` — and neither publishes a .NET SDK or a documented protocol, so the only ways in
   are to spawn a process (impossible on iOS and Android, which is the platform this server exists
   for) or to reimplement a private, versioned protocol. An ngrok provider that drove the agent was
   written and then removed for exactly that reason. The pure-managed tunnels — SSH (sish,
   localhost.run, Serveo, or your own host), Azure Relay, and the relay in the core package — are
   the ones that work on a device.
3. A JWKS endpoint and key rotation from configuration, so a fleet of servers can share an issuer.
4. Observability: an `ActivitySource`, a `Meter`, and a request-logging middleware.
5. Model validation, which would pair with the problem-details `errors` shape that is already there.
6. mDNS advertisement, so a device on the LAN can be found without being told its address.
7. Benchmarks against Kestrel to put numbers on the "dependency-light" claim.

## Notes and constraints carried forward

- **`ValueTask` in the hot path.** `RequestDelegate` returns `ValueTask` because most handlers
  complete synchronously and should not allocate to say so.
- **Contexts are pooled per connection.** Handlers must not capture `HttpContext` past their own
  return. Documented on the type.
- **Defaults are phone-safe, not server-maximal.** Binds loopback by default; an embedded server
  should not be LAN-reachable unless its author says so.
- **Roslyn pinned to 4.11** deliberately — a generator compiled against an older Roslyn loads in
  newer hosts, not the reverse.
- **`UseForwardedHeaders` is opt-in** — trusting `X-Forwarded-For` unconditionally lets any client
  spoof its address, even behind a tunnel.
- **`PublishAot` lives in the sample's csproj, not on the command line.** As a global property it
  flows into the netstandard2.0 generator project and fails the build.

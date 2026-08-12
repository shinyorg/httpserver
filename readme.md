[![Build](https://github.com/shinyorg/httpserver/actions/workflows/build.yml/badge.svg)](https://github.com/shinyorg/httpserver/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/Shiny.Net.HttpServer.svg)](https://www.nuget.org/packages/Shiny.Net.HttpServer)

# Shiny HTTP Server

ASP.NET Core is heavyweight and does not run on .NET MAUI or in several embedded server scenarios.
This is a dependency-light, fully AOT/trim-clean HTTP/1.1, HTTP/2 & HTTP/3 server that runs anywhere
.NET runs — plus tunnelling so a server embedded in a phone app is reachable from the public internet.

Only `Microsoft.Extensions.*` abstractions are taken as dependencies. Everything else — JSON, crypto,
JWT, OpenAPI, HPACK, QPACK — is built on what is in the box.

## Packages

| Package | Description |
| --- | --- |
| [Shiny.Net.HttpServer](https://www.nuget.org/packages/Shiny.Net.HttpServer) | The server: HTTP/1.1, HTTP/2 & HTTP/3, routing, middleware, DI scopes, static files, WebSockets, SSE, sessions, OpenAPI, CORS, rate limiting, IP filtering, tunnelling. Includes the typed-endpoint source generator |
| [Shiny.Net.HttpServer.Jwt](https://www.nuget.org/packages/Shiny.Net.HttpServer.Jwt) | JWT authentication on in-box crypto — no `Microsoft.IdentityModel` dependency |
| [Shiny.Net.HttpServer.AzureRelay](https://www.nuget.org/packages/Shiny.Net.HttpServer.AzureRelay) | Azure Relay tunnel provider |
| [Shiny.Net.HttpServer.Ssh](https://www.nuget.org/packages/Shiny.Net.HttpServer.Ssh) | SSH remote-forwarding tunnel provider, including zero-account quick tunnels |
| [Shiny.Net.HttpServer.Mcp](https://www.nuget.org/packages/Shiny.Net.HttpServer.Mcp) | Model Context Protocol (Streamable HTTP) transport — host an MCP server without ASP.NET Core, including inside a MAUI app |
| [Shiny.Net.HttpServer.Mediator](https://www.nuget.org/packages/Shiny.Net.HttpServer.Mediator) | Publishes Shiny.Mediator requests, commands and streams as endpoints generated at compile time. Generator included |
| [Shiny.Net.HttpServer.DocumentDb](https://www.nuget.org/packages/Shiny.Net.HttpServer.DocumentDb) | Publishes a Shiny.DocumentDb type as a REST resource — list, by-id, count, CRUD, merge-patch and a live SSE tail |
| [Shiny.Net.HttpServer.WebDav](https://www.nuget.org/packages/Shiny.Net.HttpServer.WebDav) | A WebDAV (RFC 4918) class 1 & 2 server over a directory — mount an app's storage in Finder, Windows Explorer or any WebDAV client |
| [Shiny.Net.HttpServer.Grpc](https://www.nuget.org/packages/Shiny.Net.HttpServer.Grpc) | gRPC and gRPC-Web — unary, streaming and bidirectional methods over the same HTTP/2 stack, with serialization you supply |

## Getting Started

```csharp
var server = new HttpServer(new HttpServerOptions { Port = 8080 });
server.MapGet("/ping", ctx => ctx.Response.WriteAsync("pong"));
await server.RunAsync();
```

Typed endpoints, generated at compile time:

```csharp
[Route("/api/users")]
public class UserEndpoints(IUserService users, ILogger<UserEndpoints> logger)
{
    [Get("/{id:int}")]
    public async Task<IActionResult> GetUser(int id, CancellationToken ct)
        => await users.FindAsync(id, ct) is { } u ? new OkObjectResult(u) : new NotFoundResult();
}

app.MapMyAppEndpoints();   // emitted for every [Route] class in the assembly
```

An MCP server, on the same host, reachable from a MAUI app:

```csharp
builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "thermostat", Version = "1.0.0" })
    .WithTools<ThermostatTools>()
    .WithHttpTransport();

var app = builder.Build();
app.MapMcp();              // POST/GET/DELETE/OPTIONS on /mcp
```

The MCP package is trim- and AOT-clean like the rest, with one thing the compiler cannot check for
you: a tool's parameter and return types are published as a JSON schema, and building that schema by
reflection does not survive trimming. Tools that trade only in primitives need nothing extra; give
the rest a source-generated context, and `MapMcp()` will tell you if you missed one.

```csharp
[JsonSerializable(typeof(Query))]
[JsonSerializable(typeof(IReadOnlyList<Reading>))]
public partial class ToolJson : JsonSerializerContext;

.WithTools<ThermostatTools>(ToolJson.Default.Options)
```

## What is in the box

**The four tiers** — one delegate, raw routes, middleware, and source-generated typed endpoints. Each
is built on the one below and they compose in the same app.

| | |
| --- | --- |
| **Core** | Routing with constraints and runtime-mutable routes, ASP.NET-shaped middleware, a real `IServiceScope` per request, results in both `Results.*` and `IActionResult` spellings, RFC 9457 problem details and an exception-handler chain |
| **Formats** | Content negotiation in both directions — responses chosen from `Accept`, request bodies from `Content-Type`. JSON out of the box; XML, MessagePack and protobuf are one line each, and a format of your own is an `IOutputFormatter`/`IInputFormatter` pair. XML and MessagePack need no dependency and no attributes on your DTOs: they read the same `JsonTypeInfo` the JSON path reads, which is what keeps them AOT-clean where `XmlSerializer` cannot be |
| **Protocols** | HTTP/1.1, HTTP/2 (own HPACK), HTTP/3 (own QPACK), WebSockets, Server-Sent Events, trailing headers on all three versions. Never guessed — ALPN over TLS, connection preface over cleartext |
| **Content** | Static files from disk *or* embedded resources, a published Blazor WebAssembly app, streaming multipart uploads, downloads with byte ranges and conditional GETs, a file browser over a directory, and brotli/gzip/deflate compression |
| **Security** | Authentication and authorization split ASP.NET-style, with Basic, API key, cookie and JWT schemes; policies, roles and claims; CORS, rate limiting and IP filtering, all with per-endpoint policies |
| **TLS** | Several endpoints with per-endpoint TLS, self-signed certificates generated in managed code (iOS and Android included), client certificates, and SPKI pinning for the app's own `HttpClient` |
| **OpenAPI** | An OpenAPI 3.0.3 document built entirely from compile-time metadata and your `JsonSerializerContext` — no reflection, no document object model |
| **Tunnelling** | A pluggable `ITunnelProvider`, the reference relay (both ends), SSH remote forwarding, zero-account quick tunnels, and Azure Relay |
| **Mediator** | Shiny.Mediator handlers published as endpoints — requests as JSON, commands as a status code, stream requests as Server-Sent Events, all bound at compile time |
| **DocumentDb** | A document type as a complete HTTP resource, with filtering, cursor paging, sparse fieldsets, ETag/If-Match, RFC 7396 merge-patch, a live SSE tail, and server-side scopes enforced on both sides of a write |
| **gRPC** | Unary, client-streaming, server-streaming and bidirectional methods, deadlines, per-message compression and status in trailers — plus gRPC-Web for browsers and anything on HTTP/1.1. Marshalling is yours, so nothing reflects over your messages |
| **WebDAV** | RFC 4918 classes 1 and 2 over a directory — `PROPFIND`, `PROPPATCH`, `MKCOL`, `COPY`, `MOVE`, `LOCK`/`UNLOCK`, the `If` header and dead properties — so an app's storage mounts as a drive with no client to write |
| **Lifecycle** | Start, stop and restart at runtime, serialized and idempotent, with an observable state — an embedded server gets toggled, not just booted |

Everything shipping targets `net10.0` with the trim, AOT and single-file analyzers enabled, so
"AOT-clean" is enforced by the build rather than claimed in a readme.

## Documentation

Full docs are at [shinylib.net/httpserver](https://shinylib.net/httpserver).

## Support

Shiny is free and will continue to be, but maintenance and support take a heavy toll on
sustainability. If you or your company have the resources, please consider
[becoming a GitHub Sponsor](https://sponsor.shinylib.net).

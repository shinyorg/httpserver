# Shiny.Net.HttpServer.Mcp

Hosts a Model Context Protocol server on `HttpServer` — the MCP Streamable HTTP transport, without
ASP.NET Core.

The MCP C# SDK ships its HTTP transport as `ModelContextProtocol.AspNetCore`, which needs a
`WebApplication`. That rules out every place ASP.NET Core will not go, and the most interesting of
those is a phone. This package is the same transport over this server instead, so an MCP server can
run *inside* a .NET MAUI app and expose the device it is running on.

The protocol itself is not reimplemented here. `ModelContextProtocol.Core` factors
`StreamableHttpServerTransport` to be host-agnostic — it reads and writes plain `Stream`s — so what
this package supplies is the HTTP around it: routing, sessions, the origin guard, and the status
codes.

## Wire it up

```bash
dotnet add package Shiny.Net.HttpServer.Mcp
```

```csharp
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Mcp;

var builder = HttpServer.CreateBuilder();
builder.Configure(o => o.Port = 8181);

builder.Services.AddSingleton<Thermostat>();
builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "thermostat", Version = "1.0.0" })
    .WithTools<ThermostatTools>()
    .WithHttpTransport(o => o.AllowedOrigins.Add("http://localhost:6274"));

var app = builder.Build();
app.MapMcp();                       // http://host:8181/mcp

await app.RunAsync(token);
```

Tools, prompts and resources are configured entirely through the SDK's own `AddMcpServer()` builder —
`WithTools<T>()`, `WithToolsFromAssembly()`, `WithPrompts<T>()`, handlers, filters. Nothing about
that changes here. `WithHttpTransport()` is deliberately the same call the SDK's ASP.NET package
uses, so registration reads identically on both hosts.

Tools resolve services from the server's container, because the MCP server is created from it:

```csharp
[McpServerTool(Name = "get_temperature"), Description("Reads the current temperature.")]
public static string GetTemperature(Thermostat thermostat) => $"{thermostat.Current:0.0}°C";
```

See `samples/Sample.Mcp` for a runnable version. Point the MCP Inspector at
`http://localhost:8181/mcp`, or `curl` it directly:

```bash
curl -s -X POST http://localhost:8181/mcp \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

## What the endpoint is

One path, four verbs:

| Verb      | What it does                                                                    |
|-----------|---------------------------------------------------------------------------------|
| `POST`    | A JSON-RPC message in. Out comes an SSE stream of responses, or `202` if the client only sent notifications. |
| `GET`     | Opens the stream the server uses to speak first — sampling, elicitation, roots, notifications. `405` when turned off. |
| `DELETE`  | Ends a session.                                                                 |
| `OPTIONS` | Browser preflight.                                                              |

`MapMcp()` returns the four routes as a set, so a convention lands on all of them at once:

```csharp
app.MapMcp().RequireAuthorization("agents");
```

The preflight stays anonymous — browsers send `OPTIONS` without credentials, so requiring auth there
would make the endpoint unreachable from a browser rather than more secure. Pair it with
`Shiny.Net.HttpServer.Jwt` for the bearer half of the MCP authorization spec.

## Sessions, and when you get one

Worth understanding, because it is not what the older spec text suggests:

- **Request carries `Mcp-Session-Id`** → that session. Unknown or expired is `404`, which is how a
  client knows to start over rather than give up.
- **No session id, and the message is `initialize`** → a session is created and its id returned. This
  is the classic handshake.
- **No session id, anything else** → answered on a per-request server that is thrown away afterwards.

That last case is the one that matters in practice: SDK 2.x clients connect through `server/discover`
and never send `initialize`, so they run session-less. The SDK's own ASP.NET server behaves the same
way — it returns no session id at all in that flow. Rejecting session-less requests would lock out
every current client.

The consequence is that state held between tool calls, and anything needing the `GET` stream, only
applies to clients that do initialize. Set `Stateless = true` to refuse sessions outright, which is
what you want behind a load balancer where the next request may not reach this process.

Sessions are reclaimed after `IdleSessionTimeout` (30 minutes) and capped by `MaxSessions` (32) —
exceeding it answers `429`. A session with a request still open is never idle, so a `GET` stream
sitting silent for an hour is not collected out from under its client.

## The origin guard

A request carrying an `Origin` header is coming from a page, and is refused unless that origin was
named. A request without one is not from a browser and passes untouched — that is every native MCP
client.

```csharp
o.AllowedOrigins.Add("https://inspector.example.com");
o.AllowAnyOrigin = true;                       // development only
o.OriginValidator = origin => …;               // or decide it yourself
```

This is not decoration. An MCP server bound to localhost is otherwise reachable by any site the user
happens to visit, which is the DNS-rebinding attack the MCP spec warns about. Allowed origins get
the CORS headers they need, including `Mcp-Session-Id` exposed and accepted.

## Reaching it from outside

An MCP server on a device is only useful if a client can get to it. Nothing about this package binds
you to a local port — pair it with `Shiny.Net.HttpServer.Ssh` or `Shiny.Net.HttpServer.AzureRelay`
and the same endpoint is reachable through a tunnel, from a device with no routable address at all.

## Trade-offs worth knowing

**This package is not AOT- or trim-clean, and the core server still is.** The SDK discovers tools by
reflecting over attributes, and `Microsoft.Extensions.AI.Abstractions` comes along with it. Reference
this and you accept the weight; don't, and the server stays a few megabytes of AOT.

**`MCP-Protocol-Version` is accepted and ignored.** Requests are not rejected for naming an unknown
version. Forward-compatible, but not strict.

**A session id is a bearer token in everything but name.** It is 128 bits of `RandomNumberGenerator`
for that reason. Anyone holding one can speak as that client, so put the endpoint behind
authorization if the network it is on is not one you trust.

**Every session is a live `McpServer` with a running message pump.** That is what `MaxSessions` is
for. On a phone the ceiling matters more than it does on a server.

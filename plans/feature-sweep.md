# Feature sweep — everything from the gap review

Ordered by phase. Each phase ends green: `dotnet build build.slnf` + `dotnet test`.

## Phase 1 — core hygiene
- [x] Telemetry: Meter + ActivitySource, W3C traceparent in/out (`Telemetry/`)
- [x] Health checks + `MapHealthChecks` (`HealthChecks/`)
- [x] Request decompression (`Compression/`)
- [x] Request timeouts, policy + attribute (`Timeouts/`)

## Phase 2 — API surface
- [x] ETag/conditional helpers for non-file responses + output caching (`Caching/`)
- [x] Security headers, HSTS, HTTPS redirect (`Security/`)
- [x] Antiforgery (`Security/Antiforgery/`)
- [x] `MapProxy` forwarder (`Proxy/`)

## Phase 3 — protocol/format
- [x] WebSocket registry/groups/broadcast, keepalive, permessage-deflate (`WebSockets/`)
- [x] MCP OAuth protected-resource metadata (RFC 9728) — Mcp package
- [x] Agent tunnel providers: cloudflared / ngrok / tailscale — new package
- [x] WebTransport / HTTP3 datagrams — feasibility first (System.Net.Quic has no datagram API)

## Phase 4 — mobile
- [x] `Shiny.Net.HttpServer.Discovery` — mDNS advertise/browse on Shiny.Net.Discovery
- [x] `Shiny.Net.HttpServer.Mobile` — Shiny.Core lifecycle, Android foreground service, iOS local-network check
- [x] Network-change rebinding (core, NetworkChange; IConnectivity on mobile)

## Phase 5 — testing
- [x] `Shiny.Net.HttpServer.Testing` — in-memory transport over DuplexPipeConnection

## Follow-on request
- [x] W3C extended log file middleware (`Logging/`) — `UseW3CLogging`, rolling files, bounded queue

## Phase 6 — the other three artifacts
- [x] docs site pages + release notes (~/Desktop/dev/documentation)
- [x] skills/shiny-httpserver/SKILL.md
- [x] readme.md

## Status — all phases implemented

Everything above is done and green: `dotnet build build.slnf` clean in Release with no IL
warnings, `dotnet test` at 1461 passing, and `dotnet publish -r osx-arm64` on Sample.Api still
AOT-clean.

Two things deliberately did not land as originally sketched:

1. **WebTransport / HTTP datagrams - not implementable.** `System.Net.Quic` in .NET 10 exposes no
   datagram API at all (no send, no receive, nothing on `QuicConnection` or its options), and both
   RFC 9297 HTTP Datagrams and WebTransport are defined on top of QUIC datagram frames. Nothing in
   this repo can add one without reimplementing the QUIC layer under msquic. The limitation is now
   documented on `Http3Options` so it is discoverable where someone would look for it.

2. **`Shiny.Net.HttpServer.Mobile` is in the solution but not in `build.slnf`.** It multi-targets
   net10.0-android/ios/maccatalyst, and the CI job provisions the SDK without those mobile
   workloads, so adding it to the filter would fail the build. To ship it, the CI job needs a step
   that provisions the android/ios/maccatalyst workloads before the build step, and the project
   then goes back into `build.slnf`.

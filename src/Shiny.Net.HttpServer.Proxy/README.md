# Shiny.Net.HttpServer.Proxy

A reverse proxy for `Shiny.Net.HttpServer` — destination clusters, load balancing, health checks,
session affinity, transforms, WebSocket forwarding, and routes read from `IConfiguration`.

Trim- and AOT-clean, and dependency-light in the same way the server is: no reflection-based
configuration binding, no `Microsoft.Extensions.Configuration.Binder`, nothing that stops working
when the app is published with trimming on.

## Why an embedded server has a proxy at all

The device is often the only thing that can reach what the caller wants.

- A phone bridges its own loopback services out through a [tunnel](https://shinylib.net/httpserver/tunneling/).
- A Raspberry Pi fronts a printer or a camera that speaks HTTP with no TLS and no authentication —
  the Pi adds both.
- A dev server serves the app and forwards `/api` to the real backend, so the browser sees one origin
  and CORS never comes up.
- A gateway on the LAN spreads requests over two or three instances of a service and takes one out of
  rotation when it stops answering.

## Four escalating shapes

### 1. One destination

```csharp
app.MapProxy("/api/{*path}", "https://api.example.com");
```

Bodies stream both ways, `X-Forwarded-For`/`-Proto`/`-Host` describe the original caller, an
unreachable upstream is a `502` and one that will not answer is a `504`.

### 2. A cluster

```csharp
app.MapProxy("/api/{*path}", cluster =>
{
    cluster.AddDestination("a", "https://a.internal");
    cluster.AddDestination("b", "https://b.internal");

    cluster.LoadBalancing = LoadBalancingPolicy.PowerOfTwoChoices;

    cluster.HealthCheck.Active.Enabled = true;
    cluster.HealthCheck.Active.Path = "/health";

    cluster.SessionAffinity.Mode = SessionAffinityMode.Cookie;
});
```

Policies: `PowerOfTwoChoices` (default), `RoundRobin`, `LeastRequests`, `Random`, `First`. Implement
`ILoadBalancingPolicy` and set `cluster.LoadBalancer` for one of your own.

Health is tracked in two independent places. The **passive** check watches real traffic and takes a
destination out after `FailureThreshold` consecutive failures, for `ReactivationPeriod`. The
**active** check probes on a timer whether or not any traffic is flowing; it is off by default,
because a probe every few seconds on a battery-powered device is not free. When every destination is
out, the route answers `503`.

The single-destination shorthand builds a cluster of one with the passive check **off**: quarantining
the only destination there is would turn a `502` that names a real upstream failure into a `503` that
names nothing.

### 3. Transforms

```csharp
cluster.Transforms
    .RemovePathPrefix("/api")
    .SetQueryValue("tenant", "acme")
    .SetRequestHeader("X-Api-Key", key)
    .RemoveResponseHeader("Server")
    .WithForwardedHeaders(ForwardedHeadersMode.Append);
```

Request transforms run after the default path and query have been worked out and before the request
goes; response transforms run after the upstream's headers have been copied onto the response and
before the body streams back. `AddRequestTransform` / `AddResponseTransform` take a delegate for
anything the built-ins do not cover.

### 4. Configuration

```csharp
builder.AddReverseProxy(configuration.GetSection("ReverseProxy"));
```

```json
{
  "ReverseProxy": {
    "Routes": {
      "api": {
        "ClusterId": "backend",
        "Match": { "Path": "/api/{**rest}", "Hosts": [ "*.example.com" ] },
        "Transforms": [
          { "PathRemovePrefix": "/api" },
          { "RequestHeader": "X-Tenant", "Set": "acme" }
        ]
      }
    },
    "Clusters": {
      "backend": {
        "LoadBalancingPolicy": "LeastRequests",
        "HealthCheck": { "Active": { "Enabled": true, "Interval": 10, "Path": "/health" } },
        "Destinations": {
          "d1": { "Address": "https://a.internal" },
          "d2": { "Address": "https://b.internal" }
        }
      }
    }
  }
}
```

A catch-all may be written `{**rest}` or `{*rest}` — a configuration file copied over from YARP
should not fail to parse over a star.

**Reload works.** Routes in this server are not frozen when it starts, so a configuration change is
applied to the running server: routes are swapped atomically, clusters that keep their id keep their
destinations' health state, and routes mapped in code are left alone. A file saved half-written is
logged and ignored rather than being allowed to tear down a working proxy.

## WebSockets

A `Upgrade` request is completed against the upstream and the connection is then handed over
outright, so a WebSocket, and anything else that upgrades on HTTP/1.1, is forwarded end to end. On by
default; `options.ForwardUpgrades = false` turns it off.

## Notes

- The default outbound client has redirects off, cookies off, and no automatic decompression:
  following a redirect on the caller's behalf hides it from them, and decompressing here only to
  recompress on the way out spends the device's battery to change nothing. Set `options.Client` to
  supply your own.
- `HttpForwarder.ForwardAsync(context, "http://…")` forwards from inside an ordinary handler, for a
  route that does its own deciding.
- Full documentation: <https://shinylib.net/httpserver/proxy/>

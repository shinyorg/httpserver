# Shiny.Net.HttpServer.AzureRelay

Gives an embedded `HttpServer` a public HTTPS address without any inbound connectivity.

The device dials **out** to Azure and holds the connection open; Azure owns the public endpoint and
forwards traffic down it. That is what makes this work on a phone: a cellular device sits behind
carrier-grade NAT with no routable address and no port you could forward, so nothing can connect
*to* it — but it can always connect *out*.

```
curl ──▶ https://contoso.servicebus.windows.net/device-1/api/widgets
                              │
                        Azure Relay
                              │  (outbound TLS, opened by the device)
                              ▼
                    your app's HttpServer
```

## Which mode

| | `Http` (default) | `RelayedStream` |
|---|---|---|
| Caller needs | nothing — curl, a browser, a webhook | `HybridConnectionClient` (an Azure SDK) |
| Public URL | `https://{namespace}/{name}` | `sb://{namespace}/{name}` |
| Request/response | ✅ | ✅ |
| Keep-alive, pipelining | relay's business | ✅ real connections |
| WebSockets, SSE, streaming | ❌ (see below) | ✅ |
| Response buffering | whole body buffered | none |

`Http` mode is a request/response bridge: the relay hands over a parsed request and wants a status
code, headers and a body back, so each response is read to completion before it is returned. Fine
for an API; useless for SSE (an endpoint that never ends would never respond) and impossible for
WebSockets. If you need those over the relay, use `RelayedStream` and give callers a client.

Nothing stops you from doing both — run the server on a local port for the LAN and a relay tunnel
for the outside world, from the same routes.

---

## 1. Create the Azure resources

Relay has one SKU (Standard) and hybrid connections are billed per *listener*, so this is cheap to
leave running. Check current [Relay pricing](https://azure.microsoft.com/pricing/details/service-bus/)
before you fan it out to a fleet.

### With the az CLI

```bash
RG=my-rg
NS=contoso                 # becomes contoso.servicebus.windows.net — must be globally unique
HC=device-1                # one hybrid connection per device

az group create --name $RG --location eastus

az relay namespace create \
  --resource-group $RG \
  --name $NS \
  --location eastus

# requires-client-authorization decides whether *callers* must present a token.
#   true  → callers send a Send-rights SAS token (see step 5)
#   false → anyone who knows the URL can reach your device
az relay hyco create \
  --resource-group $RG \
  --namespace-name $NS \
  --name $HC \
  --requires-client-authorization true
```

### Or in the portal

1. **Create a resource → Integration → Relay**, pick a resource group and a globally unique
   namespace name, create it.
2. Open the namespace → **Hybrid Connections** → **+ Hybrid Connection**.
3. Name it (`device-1`) and choose whether to **Requires Client Authorization**.

## 2. Create the SAS policies

Two separate policies, because the device and the caller need different rights. Do not give the
device `Send`, and do not give callers `Listen`.

```bash
# The device: may attach as a listener, nothing else.
az relay hyco authorization-rule create \
  --resource-group $RG --namespace-name $NS --hybrid-connection-name $HC \
  --name listen-only --rights Listen

# Callers, only if requires-client-authorization is true.
az relay hyco authorization-rule create \
  --resource-group $RG --namespace-name $NS --hybrid-connection-name $HC \
  --name send-only --rights Send
```

Get the connection strings:

```bash
az relay hyco authorization-rule keys list \
  --resource-group $RG --namespace-name $NS --hybrid-connection-name $HC \
  --name listen-only --query primaryConnectionString -o tsv
```

which prints something like

```
Endpoint=sb://contoso.servicebus.windows.net/;SharedAccessKeyName=listen-only;SharedAccessKey=…;EntityPath=device-1
```

In the portal the same thing is under the hybrid connection → **Shared access policies**.

## 3. Wire it up

```bash
dotnet add package Shiny.Net.HttpServer.AzureRelay
```

```csharp
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.AzureRelay;

// The relay is the only way in here, so the server is never started locally — RunTunnelAsync
// binds nothing. Call StartAsync as well if you also want a LAN listener.
var server = new HttpServer(new HttpServerOptions());
server.MapGet("/api/widgets", ctx => Results.Ok(widgets, AppJson.Default.WidgetArray));

var tunnel = new AzureRelayTunnelProvider(new AzureRelayOptions
{
    ConnectionString = "Endpoint=sb://contoso.servicebus.windows.net/;SharedAccessKeyName=listen-only;SharedAccessKey=…;EntityPath=device-1"
});

Console.WriteLine($"Reachable at {tunnel.PublicUrl}");
await server.RunTunnelAsync(tunnel, cancellationToken: token);
```

With a container — a generic host, a MAUI app, a Shiny host:

```csharp
builder.Services.AddHttpServer(
    configureServer: server => server.MapGet("/api/widgets", …),
    autoStart: false           // relay only; drop this to also listen locally
);

builder.Services.AddAzureRelayTunnel(o =>
{
    o.ConnectionString = configuration["Relay:ConnectionString"];
    o.HybridConnectionName = "device-1";
});
```

`autoStart: false` on either registration leaves it configured but idle. Resolve `AzureRelayTunnel`
and start it when the user asks — which is what an app with a *Remote access* toggle wants:

```csharp
public sealed class RemoteAccessViewModel(AzureRelayTunnel tunnel)
{
    public async Task ToggleAsync(bool on) =>
        this.Url = on ? await tunnel.StartAsync() : null;
}
```

## 4. Check it

```bash
curl https://contoso.servicebus.windows.net/device-1/api/widgets
```

Note the path: Azure addresses a device as `https://{namespace}/{hybridConnectionName}/{path}`, but
by default the provider strips the connection name before routing, so `/api/widgets` matches the
route you mapped. Set `StripHybridConnectionNameFromPath = false` if you would rather see the raw
path.

`RelayedStream` mode is dialled instead of curled:

```csharp
var client = new HybridConnectionClient(connectionStringWithSendRights);
await using var stream = await client.CreateConnectionAsync();
// Speak HTTP/1.1 into `stream`.
```

## 5. If client authorization is required

Callers pass a Send-rights SAS token. Use the **`ServiceBusAuthorization`** header rather than
`Authorization` — the relay consumes it and does not forward it, leaving `Authorization` free for
your app's own JWT:

```bash
curl https://contoso.servicebus.windows.net/device-1/api/widgets \
  -H "ServiceBusAuthorization: SharedAccessSignature sr=…&sig=…&se=…&skn=send-only" \
  -H "Authorization: Bearer $YOUR_APP_TOKEN"
```

Mint one with `AzureRelaySas.Create` (see below), passing the **send** policy.

---

## Don't ship the key

A shared access key inside a mobile app is a published key. Anyone who unzips the package has it,
and a `Listen` key lets them attach as a listener on your hybrid connection and answer requests as
if they were the device.

The shape that survives review: the **backend** holds the key and mints a short-lived token scoped
to one hybrid connection; the device asks for one over its authenticated API.

On the backend:

```csharp
[HttpGet("relay-token")]                            // behind your own auth
public string GetRelayToken() => AzureRelaySas.Create(
    "contoso.servicebus.windows.net",
    hybridConnectionName: this.User.GetDeviceId(),  // each device gets only its own
    keyName: "listen-only",
    key: configuration["Relay:ListenKey"]!,
    validFor: TimeSpan.FromHours(8)
);
```

On the device:

```csharp
new AzureRelayOptions
{
    Namespace = "contoso.servicebus.windows.net",
    HybridConnectionName = deviceId,
    RefreshSharedAccessSignature = ct => api.GetRelayTokenAsync(ct)
}
```

`RefreshSharedAccessSignature` is called each time the listener is established, so a reconnect after
the token expires picks up a fresh one. `AzureRelaySas.GetExpiry` tells you when that will be if you
want to refresh ahead of it.

The relay authenticates the *tunnel*, not your users. It says who may attach and who may call —
nothing about who is making the request. Keep your own authentication and authorization in the
pipeline; it runs on relayed requests exactly as on local ones.

## Batteries

- `IsOnline` / `ConnectivityChanged` — the relay drops and reconnects on its own (a phone changing
  networks, a tunnel idling out). Surface it rather than assuming the tunnel is up.
- `KeepAliveInterval` — leave it null unless you have a reason. On cellular, longer is kinder: every
  ping wakes the radio, and reconnecting after an idle timeout costs far less battery than never
  going idle.
- `Authorize` — reject a rendezvous before it becomes a connection (`RelayedStream` mode only). In
  `Http` mode, authorize in the pipeline like any other request.
- `MaxResponseHeadSize` — the cap on a buffered response head while relaying, 64 KB by default.

## Trade-offs worth knowing

**This package is not AOT- or trim-clean, and the core server still is.** `Microsoft.Azure.Relay`
pulls in Azure.Identity, MSAL and IdentityModel, none of which trim. That is exactly why this lives
in its own package: reference it and you accept the weight; don't, and the server stays a few
megabytes of AOT.

**`Http` mode buffers.** A 500 MB download is a 500 MB buffer. Use `RelayedStream` for large or
streaming payloads, or serve those over the LAN.

**Latency is a round trip to Azure.** Every request goes device → Azure → caller. Pick a region near
your users, and prefer the local listener when both ends are on the same network.

**One hybrid connection per device.** Azure does allow several listeners on one connection, but it
dispatches to one of them arbitrarily — so two devices sharing a connection means half your requests
reach the wrong phone. Name the connection after the device.

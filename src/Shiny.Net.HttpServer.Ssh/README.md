# Shiny.Net.HttpServer.Ssh

Publishes an embedded `HttpServer` through SSH remote port forwarding — `ssh -R`, in library form.

The device opens an ordinary outbound SSH connection and asks the server to forward a port back down
it. Nothing connects *to* the device, which is what makes it work on a phone: a cellular device sits
behind carrier-grade NAT with no routable address and no port you could forward.

```
curl ──▶ https://device-1.example.com          (nginx/Caddy, TLS)
                    │
              127.0.0.1:8080                   (the forwarded port on your VPS)
                    │  ssh -R, opened by the device
                    ▼
          127.0.0.1:{ephemeral}                (loopback socket this package owns)
                    │
            your app's HttpServer
```

The tunnel binds its own ephemeral loopback socket and hands what arrives to the server, so the app
never binds a port of its own. Only processes on the device can reach that socket.

Three ways to use it. In a hurry, skip to **[the one-liner](#the-one-liner-a-public-url-with-no-account)**:
a public URL on a phone with no account and nothing installed. Otherwise: **a server you own** (a $5
VPS — stable hostname, your TLS, your rules) or **a hosted tunnel** configured by hand.

---

## A. A server you own

### 1. Create a user that can do nothing but forward

Do not tunnel as a user with a shell. A forwarding account needs no shell, no filesystem, no
commands — and if the device is compromised, that limit is the only thing standing between the
attacker and your box.

```bash
sudo adduser --system --shell /usr/sbin/nologin --no-create-home tunnel
sudo mkdir -p /home/tunnel/.ssh && sudo chown -R tunnel: /home/tunnel
```

### 2. Give the device a key

On the device's build machine (never on the server):

```bash
ssh-keygen -t ed25519 -f device-1 -N '' -C 'device-1 tunnel'
```

Add the public key to the server, restricted to exactly what it needs — no shell, no agent, no X11,
and only the one port it may bind:

```bash
# /home/tunnel/.ssh/authorized_keys
restrict,port-forwarding,permitlisten="127.0.0.1:8080" ssh-ed25519 AAAAC3Nza… device-1
```

`restrict` denies everything and then `port-forwarding` adds back the one capability needed.
`permitlisten` stops a stolen key from binding any port other than 8080. One key and one port per
device.

### 3. Allow remote forwarding in sshd

```bash
# /etc/ssh/sshd_config
AllowTcpForwarding remote      # remote (-R) only; the device never needs -L
GatewayPorts no                # forwarded ports bind loopback, not the internet

ClientAliveInterval 30         # notice a dead device and free its port
ClientAliveCountMax 2

Match User tunnel
    PermitTTY no
    X11Forwarding no
    AllowAgentForwarding no
```

```bash
sudo sshd -t && sudo systemctl reload ssh
```

`GatewayPorts no` is the right default: the forwarded port stays on loopback and a reverse proxy in
front of it terminates TLS. Setting `GatewayPorts yes` (or `clientspecified`) publishes the port on
the public interface directly — plaintext HTTP on an odd port, no certificate. Only do that for a
throwaway.

### 4. Terminate TLS in front of it

With Caddy, which gets a certificate on its own:

```
device-1.example.com {
    reverse_proxy 127.0.0.1:8080
}
```

Or nginx:

```nginx
server {
    server_name device-1.example.com;
    listen 443 ssl;                                # certbot fills in the cert lines

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # WebSockets and SSE, if you serve them
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_read_timeout 1h;
        proxy_buffering off;                       # SSE needs this
    }
}
```

### 5. Pin the host key

Get the fingerprint from a machine you trust, ideally the server itself:

```bash
# on the server
ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub

# or remotely, if you accept the first-contact risk
ssh-keyscan -t ed25519 tunnel.example.com | ssh-keygen -lf -
```

Both print `256 SHA256:47DEQpj8HBSa+…  no comment (ED25519)`. The `SHA256:…` part is what you pin.

### 6. Wire it up

```bash
dotnet add package Shiny.Net.HttpServer.Ssh
```

```csharp
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Ssh;

// The tunnel is the only way in here, so the server is never started locally.
var server = new HttpServer();
server.MapGet("/api/widgets", ctx => Results.Ok(widgets, AppJson.Default.WidgetArray));

var tunnel = new SshTunnelProvider(new SshTunnelOptions
{
    Host = "tunnel.example.com",
    Username = "tunnel",
    PrivateKey = await SecureStorage.GetKeyBytesAsync(),   // wherever you keep it
    RemoteBindAddress = "127.0.0.1",                       // matches permitlisten
    RemotePort = 8080,
    PublicUrl = "https://device-1.example.com",            // what the proxy answers on
    HostKeyFingerprints = { "SHA256:47DEQpj8HBSa+…" }
});

await server.RunTunnelAsync(tunnel, cancellationToken: token);
```

Because a proxy sits in front, tell the server to believe its forwarding headers — otherwise every
request looks like it came from `127.0.0.1` over plain HTTP:

```csharp
var server = new HttpServer(new HttpServerOptions { UseForwardedHeaders = true });
```

Only with a proxy you control. The header is a claim by whoever sent it, and believing it from an
arbitrary client lets any caller pick its own IP.

---

## The one-liner: a public URL with no account

For a phone that needs to hand someone a link, none of the setup above is required:

```csharp
builder.Services.AddHttpServer(configureServer: s => s.MapGet("/", …), autoStart: false);
builder.Services.AddQuickTunnel();          // localhost.run by default; nothing to sign up for
```

`QuickTunnel` implements `INotifyPropertyChanged`, so a view binds straight to it:

```csharp
public sealed class SharingViewModel : INotifyPropertyChanged
{
    readonly QuickTunnel tunnel;

    public SharingViewModel(QuickTunnel tunnel)
    {
        this.tunnel = tunnel;

        // Raised on a background thread. MAUI will not marshal this for you.
        tunnel.PropertyChanged += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            this.Url = tunnel.PublicUrl;              // show it, or render it as a QR code
            this.Status = tunnel.State.ToString();
            this.OnPropertyChanged(nameof(this.Url));
            this.OnPropertyChanged(nameof(this.Status));
        });
    }

    public string? Url { get; private set; }
    public string? Status { get; private set; }

    public Task ShareAsync() => this.tunnel.StartAsync();
    public Task StopSharingAsync() => this.tunnel.StopAsync();
}
```

**Bind to `PublicUrl`; do not read it once.** A free tunnel assigns a *different address on every
reconnect*, and a phone reconnects whenever it changes network. An app that painted the first URL on
a label would be showing a dead link a few minutes later. `State` goes `Connecting` → `Connected` →
`Reconnecting` → `Connected`, and `PublicUrl` is cleared the moment the connection drops — showing
nothing beats showing a link that no longer works.

For a console app or a sample:

```csharp
await app.RunQuickTunnelAsync(url => Console.WriteLine($"Reachable at {url}"));
```

Three hosts are preset. `QuickTunnelHost.LocalhostRun` needs nothing at all;
`QuickTunnelHost.Sish` derives the subdomain from your key, so the same key gets the same address —
worth having if the URL goes on a label or into someone's bookmarks. Pass a `subdomain` to request
one.

**What you are accepting.** These hosts publish no stable key to pin, so `AcceptAnyHostKey` is on,
and your traffic passes through someone else's server. That is the trade for zero setup. For
anything you would mind being logged, run your own sish or use the VPS setup below — the same
`QuickTunnel` API points at either.

## B. A hosted tunnel

Nothing to run, a URL assigned to you, and traffic through a third party. Fine for development, a
demo, or a webhook you are debugging. For anything you would be upset to see logged, run your own.

### sish

Public instance at `tuns.sh`, or self-host the container. Takes any key and derives your subdomain
from it, so the same key gets the same URL:

```csharp
var tunnel = new SshTunnelProvider(new SshTunnelOptions
{
    Host = "tuns.sh",
    Username = "anything",
    PrivateKeyPath = keyPath,
    RemoteBindAddress = "device-1",     // requested subdomain
    RemotePort = 80,
    CaptureUrlFromSession = true,       // sish prints the assigned URL
    HostKeyFingerprints = { "SHA256:…" }
});

await tunnel.BindAsync(token);
Console.WriteLine(tunnel.PublicUrl);    // https://device-1.tuns.sh
```

### localhost.run

Authenticates nobody and assigns the URL:

```csharp
new SshTunnelOptions
{
    Host = "localhost.run",
    Username = "nokey",
    RemoteBindAddress = "localhost",
    RemotePort = 80,
    CaptureUrlFromSession = true,
    AcceptAnyHostKey = true             // no stable key to pin
}
```

### Serveo

```csharp
new SshTunnelOptions
{
    Host = "serveo.net",
    Username = "device-1",
    RemoteBindAddress = "device-1",     // requested subdomain
    RemotePort = 80,
    CaptureUrlFromSession = true,
    AcceptAnyHostKey = true
}
```

`CaptureUrlFromSession` is how you learn where you landed: all three print the URL on the session
channel once forwarding is up, and there is no other way to know — the address is the server's to
choose. The channel is left open afterwards, because some providers tear the forward down with it.
If nothing matches within `UrlCaptureTimeout` (15s), `PublicUrl` falls back to the forwarded port and
a warning is logged. Adjust `UrlPattern` if a provider prints several URLs and you want a specific
one.

Pin fingerprints for these too where the provider publishes them. `AcceptAnyHostKey` means anything
on the path can pose as the tunnel and read everything going through it.

---

## Starting and stopping in-app

```csharp
builder.Services.AddHttpServer(
    configureServer: server => server.MapGet("/api/widgets", …),
    autoStart: false          // tunnel only; drop this to also listen locally
);

builder.Services.AddSshTunnel(o =>
{
    o.Host = "tunnel.example.com";
    o.Username = "tunnel";
    o.PrivateKeyPath = keyPath;
    o.RemotePort = 8080;
    o.PublicUrl = "https://device-1.example.com";
    o.HostKeyFingerprints.Add("SHA256:…");
}, autoStart: false);
```

Then resolve `SshTunnel` and drive it from a toggle:

```csharp
public sealed class RemoteAccessViewModel(SshTunnel tunnel)
{
    public async Task ToggleAsync(bool on) =>
        this.Url = on ? await tunnel.StartAsync() : null;
}
```

## Batteries

- **Reconnect** is on by default, with backoff from `ReconnectDelay` to `MaxReconnectDelay`. Not
  optional on a phone: moving from Wi-Fi to cellular kills the TCP connection under the tunnel, and
  without it the app looks fine while being unreachable.
- **`KeepAliveInterval`** (30s) keeps the NAT mapping alive. Carriers drop idle mappings in a minute
  or two; longer is kinder to the battery but risks silent death between requests.
- **`IsConnected` / `ConnectivityChanged`** — surface the state rather than assuming the tunnel is up.
- **`RemotePort`** — set `RemotePort = 0` and the server allocates one, readable here after binding.
  Needs sshd to permit it, and means the public URL changes on every reconnect.
- **`LocalPort`** — the loopback port the tunnel listens on. Zero (the default) takes an ephemeral
  one; nothing else needs to find it.

## Trade-offs worth knowing

**This package is not AOT- or trim-clean, and the core server still is.** SSH.NET carries
BouncyCastle and its own algorithm registries. That is why this lives in its own package: reference
it and you accept the weight; don't, and the server stays a few megabytes of AOT.

**Host keys are checked because this package makes you check them.** SSH.NET accepts any key by
default. Here, connecting without either a pinned fingerprint or an explicit `AcceptAnyHostKey`
fails — an unverified key means anything on the path can pose as the server, and a tunnel exists to
cross networks you do not control.

**The caller's IP does not survive the forward.** SSH carries bytes, not addresses, so
`RemoteEndPoint` is the loopback end of the tunnel. If the proxy in front sets `X-Forwarded-For`,
turn on `UseForwardedHeaders` — and only then.

**Latency is a round trip to the SSH host.** Every request goes device → server → caller. Put it
near your users, and prefer a local listener when both ends are on the same network.

**One device per forwarded port.** A second device binding the same remote port is refused by sshd,
not load-balanced. One port, or one subdomain, per device.

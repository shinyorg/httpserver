# Sample.Maui — a web server in your pocket

A .NET MAUI app that runs `Shiny.Net.HttpServer` on the device, serves a real page and a small JSON
API, and publishes it to the internet through `QuickTunnel` — no account, nothing installed, no
infrastructure.

```
                    ┌─────────────────────────────┐
  anyone, anywhere  │  https://xxxx.lhr.life       │  ← the link the app shows you
        │           └──────────────┬──────────────┘
        │                          │  outbound SSH, opened by the phone
        ▼                          ▼
   ┌─────────────────────────────────────────┐
   │  the app                                │
   │    /             the page below         │
   │    /ping         "pong"  (no password)  │
   │    /api/device   model, OS, battery     │
   │    /api/notes    GET and POST           │
   │    /files        browse app storage     │
   │    /mcp          4 tools, for AI clients│
   └─────────────────────────────────────────┘
             │
             └──► every request lands on the Traffic tab, both directions
```

Three tabs:

| Tab | What it is |
| --- | --- |
| **Server** | Status, the LAN and public URLs, and the buttons that start and share |
| **Traffic** | Every request the server has answered, newest first — tap one for both sides of it |
| **Access** | The username and password visitors are asked for |

## Running it

```bash
dotnet build samples/Sample.Maui/Sample.Maui.csproj -t:Run -f net10.0-maccatalyst
dotnet build samples/Sample.Maui/Sample.Maui.csproj -t:Run -f net10.0-android
dotnet build samples/Sample.Maui/Sample.Maui.csproj -t:Run -f net10.0-ios
```

The local server starts with the app — that only exposes it to the Wi-Fi it is already on.
**Share publicly** is a deliberate tap, because it puts the device on the public internet.

## What to look at

**`MauiProgram.cs`** — the whole wiring, about twenty lines. The server binds `IPAddress.Any` on port
0 so another device on the network can reach it and two copies of the app never collide. It is
registered with `autoStart: false`; the Server tab starts it.

**`Server/RequestLog.cs`** — the part that makes the Traffic tab possible, and the shortest useful
`IHttpMiddleware` in the repo. Two things in it are worth copying:

- It goes in **first, ahead of authentication**, so requests that fail the password prompt are
  recorded too. Those are usually the ones you want to see.
- Every field is **copied out** of the `HttpContext` rather than referenced. Contexts are pooled and
  reset for the next request on the same connection, so a screen holding one would be showing
  whatever request is in flight when you look at it.

What it captures: timestamp, method, path and query, protocol, status, duration, the peer address and
port, whether the connection arrived through the tunnel, the authenticated user, and **every header
in both directions** — with `Authorization` and cookie values redacted. Bodies are not captured; the
pipeline streams those straight to and from the socket, and buffering an upload on a phone to render
it on a screen is the wrong trade.

**`ViewModels/ServerViewModel.cs`** — the threading. `QuickTunnel` raises its changes **on a
background thread**, because a reconnect happens whenever the network does, and MAUI will not marshal
that for you. Every `IMainThread` hop in that file is for that reason.

The other half of the same point: the view binds to `PublicUrl` rather than reading it once. A free
tunnel hands out a **different address on every reconnect**, so an app that captured the first URL
would be showing a customer a dead link a few minutes later. When the connection drops, `PublicUrl`
goes null and the status reads *Reconnecting…* — showing nothing beats showing a link that no longer
works.

**`Server/DeviceApi.cs`** — the endpoints, as plain delegates. `ApiJsonContext` is the app's own
`JsonSerializerContext`: a source generator cannot see another generator's output, so the app owns
it, and that is what keeps serialization reflection-free — which iOS requires, since there is no JIT
to fall back on.

**`wwwroot/index.html`** — served straight out of the assembly with `UseEmbeddedFiles`. A packaged
app has no `wwwroot` on disk to point at, so the file travels inside the DLL.

## The UI half

The screens use [`Shiny.Maui.Shell`](https://shinylib.net/maui) and `CommunityToolkit.Mvvm`. Neither
is needed to host a server — they are here because showing live traffic needs more than one screen,
and the alternative was hand-rolled navigation and `INotifyPropertyChanged` boilerplate in a sample
that is supposed to be about HTTP.

- `AppShell.xaml` is a `ShinyShell` with a `TabBar`; each tab's `Route` is the name its view model
  declares in `[ShellMap]`.
- `UseShinyShell(x => x.AddGeneratedMaps())` in `MauiProgram.cs` is the only registration. The maps
  are source-generated from those attributes, so pages, view models and routes cannot drift apart.
- Tapping a request calls `navigator.NavigateToRequestDetail(id)` — also generated, from the
  `[ShellProperty]` on `RequestDetailViewModel.RequestId`. The **id** travels, not the entry: the log
  keeps the last 200 requests, and a page that outlives its entry says so rather than showing stale
  data.
- `Server/TrafficBadge.cs` puts the unseen count on the Traffic tab. It is a service with an
  `IMauiInitializeService` hook rather than part of the Traffic view model, because the whole point of
  the badge is to say something while that tab is *not* the one on screen.

## The password

Everything except `/ping` needs one. A password is generated on first run — a well-known default on
something reachable from the internet is the same as no password — and the **Access** tab lets you
change the username and password, or generate a new one, which locks out anyone still using the old
link. It is five characters because this is a sample you read out loud; a real one would not be. Changes take effect on the very next request: the app implements
`IBasicCredentialValidator`, so the server asks it each time rather than copying credentials at
startup.

The password lives in `SecureStorage` (the keychain on Apple platforms, the encrypted preference
store on Android). Where that is unavailable the app falls back to plain `Preferences` and *says so*
under the fields rather than implying a safety it does not have.

One ordering detail worth copying: the check runs **before** the static file handler.
`RequireAuthorization` on an endpoint runs after routing, and static files are served before routing
happens — so a page served by `UseEmbeddedFiles` would be public no matter what the endpoints
required. `Server/RequireAuthentication.cs` is the ten lines that close that.

## Browsing the device's files

`/files` maps the file browser over `FileSystem.AppDataDirectory`:

```bash
curl -u admin:PASSWORD https://xxxx.lhr.life/files                 # JSON listing
curl -u admin:PASSWORD https://xxxx.lhr.life/files/reports/q3.txt  # the file itself
curl -u admin:PASSWORD -X PUT --data 'hello' https://xxxx.lhr.life/files/notes.txt
curl -u admin:PASSWORD -X DELETE https://xxxx.lhr.life/files/notes.txt
```

Writing and deleting are enabled here because the sample is a demo. They are off by default in the
module, and `MapFileBrowser` returns its endpoints so you can require a stricter policy for the
verbs that change something than for the ones that only read.

## Platform notes, learned the hard way

Each of these is a silent failure rather than an error message, which is why they are here:

- **Mac Catalyst** runs sandboxed, and the sandbox grants outgoing connections only. Listening needs
  `com.apple.security.network.server` in `Platforms/MacCatalyst/Entitlements.plist` — without it the
  bind is refused and the server simply never appears.
- **iOS 14+** gates anything touching the local network behind a permission prompt, and that includes
  serving on it. `NSLocalNetworkUsageDescription` in `Info.plist` is what makes the prompt appear;
  without the key the app is denied without ever being asked.
- **Android** needs `android.permission.INTERNET`, which the MAUI template already includes.
- **iOS suspends the app in the background**, so the server stops answering when the app is not in
  the foreground. A tunnel that has to survive that belongs in a background task or on a machine
  that stays awake.

## What this is not

The public tunnel goes through **localhost.run**, which authenticates nobody and publishes no host
key to pin — so `AcceptAnyHostKey` is on and the traffic passes through someone else's server. Fine
for a demo, a webhook, or handing a colleague a link. For anything you would mind being logged, the
same `QuickTunnel` API points at a self-hosted sish or your own VPS; see the
[SSH package README](../../src/Shiny.Net.HttpServer.Ssh/README.md).

The one account here is Basic auth over a plain-HTTP tunnel, which is fine for handing a colleague a
link and wrong for anything else: `AddApiKey` suits a script, and `AddJwtBearer` suits a fleet.

The Traffic tab is a debugging aid, not an audit log. It lives in memory, holds the last 200
requests, and is gone when the app is.

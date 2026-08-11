using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sample.Api;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Cors;
using Shiny.Net.HttpServer.Jwt;
using Shiny.Net.HttpServer.OpenApi;
using Shiny.Net.HttpServer.RateLimiting;
using Shiny.Net.HttpServer.Security;
using Shiny.Net.HttpServer.Tunneling;
using Shiny.Net.HttpServer.WebDav;

// ---------------------------------------------------------------------------
// The whole ramp, in one app. Every tier below is optional and they all compose:
//
//   Tier 0  OnRequest      one delegate, no routing
//   Tier 1  OnGet/OnPost   raw handlers behind a route template
//   Tier 2  Use            middleware, ASP.NET Core shaped
//   Tier 3  [Route]        generated typed endpoints — see WidgetEndpoints.cs
// ---------------------------------------------------------------------------

var builder = HttpServer.CreateBuilder();
builder.Configure(o =>
{
    o.Port = 8080;
    o.HideExceptionDetails = false;
});

builder.Services.AddLogging(l => l.AddSimpleConsole(c => c.SingleLine = true).SetMinimumLevel(LogLevel.Information));
builder.Services.AddSingleton<IGreeter, Greeter>();
builder.Services.AddSingleton<IWidgetStore, InMemoryWidgetStore>();
builder.Services.AddScoped<RequestId>();
builder.Services.AddSingleton<RequestTimingMiddleware>();
builder.Services.AddSingleton<IUserDirectory, InMemoryUserDirectory>();

// --- The authorization mechanics, set at registration ---
//
// The signing key is generated per run because this is a sample. A real app loads it from
// configuration or a keychain: a key that changes on restart invalidates every token already
// issued.
var signingKey = JwtSigningKey.CreateSecret();

builder.Services
    .AddAuthentication()
    .AddJwtBearer(o =>
    {
        o.Issuer = SampleAuth.Issuer;
        o.Audience = SampleAuth.Audience;
        o.SigningKey = signingKey;
    });

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("admin", p => p.RequireRole("admin"));

    // Everything else stays open here. Uncommenting this flips the whole app to deny-by-default,
    // leaving only [AllowAnonymous] endpoints reachable:
    // o.SetFallbackPolicy(p => p.RequireAuthenticatedUser());
});

// --- Who may reach the server, how often, and from where ---
builder.Services.AddCors(o =>
{
    o.AddDefaultPolicy(p => p.WithOrigins("https://app.example.com").AllowAnyHeader().AllowAnyMethod());
    o.AddPolicy("public", p => p.AllowAnyOrigin().WithMethods("GET"));
});

builder.Services.AddRateLimiter(o =>
{
    // Per caller address by default. A hundred requests a minute is generous for a demo and small
    // enough to see working.
    o.GlobalPolicy = new FixedWindowRateLimitPolicy(100, TimeSpan.FromMinutes(1));
    o.AddTokenBucket("uploads", capacity: 5, tokensPerPeriod: 1, period: TimeSpan.FromSeconds(10));
});

builder.Services.AddIpFilter(o =>
    // Nothing is filtered by default here — an "admin" policy is registered for the one route that
    // wants it. Setting a DefaultPolicy would apply to every request, including the 404s.
    o.AddPolicy("admin", p => p.AllowLoopback())
);

var app = builder.Build();

// --- Tier 2: middleware, wraps everything below ---
//
// Two forms, same contract. A lambda for something small:
app.Use((ctx, next) =>
{
    // Registered as a callback rather than set after next(): by the time the handler returns,
    // headers are on the wire and mutating them throws. OnStarting runs just before they go.
    ctx.Response.OnStarting(() =>
    {
        ctx.Response.Headers["X-Served-By"] = "Shiny";
        return ValueTask.CompletedTask;
    });

    return next(ctx);
});

// ...and a class once it has dependencies or deserves its own tests:
app.Use<RequestTimingMiddleware>();

// CORS first of all: a preflight arrives without credentials, so authentication ahead of it would
// 401 the browser's question and the real request would never be sent.
app.UseCors();

// Then the two that exist to stop work from happening — both before routing, so a throttled or
// blocked request costs nothing beyond parsing.
app.UseRateLimiter();
app.UseIpFilter();

// Identify the caller before routing; decide whether they may have the endpoint after it.
app.UseAuthentication();
app.UseAuthorization();

// --- Tier 3: generated endpoints. One call registers every [Route] class in this assembly. ---
app.MapSampleApiEndpoints();

// --- The OpenAPI document, built from the same metadata the generator wrote the binders from ---
app.MapOpenApi(configure: o =>
{
    o.Title = "Sample Widgets API";
    o.Version = "1.0.0";
    o.Description = "Demonstrates all four tiers of Shiny.Net.HttpServer.";
    o.AddBearerAuthentication();
});

// --- Tier 1: routed raw handlers ---
//
// Describe() puts a raw route in the OpenAPI document with more than just its path.
app.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong"))
   .Describe(o =>
   {
       o.Summary = "Liveness probe";
       o.Tags.Add("ops");
       o.Responses.Add(new ApiResponse { StatusCode = 200, Type = typeof(string), ContentType = "text/plain" });
   });

app.OnGet("/hello/{name}", ctx =>
{
    var greeter = ctx.GetRequiredService<IGreeter>();
    return ctx.Response.WriteAsync(greeter.Greet(ctx.Request.RouteValues["name"]!));
});

// Per-route policies. Each applies to the route just mapped, the same way RequireAuthorization does.
app.OnGet("/status", ctx => ctx.Response.WriteAsync("ok"))
   .RequireCors("public")
   .DisableRateLimiting();

app.OnGet("/admin/keys", ctx => ctx.Response.WriteAsync("nothing to see here"))
   .RequireIpFilter("admin");

// IResult with JsonTypeInfo passed directly — the most explicit AOT-safe JSON you can write.
app.OnGet("/users/{id:int}", ctx =>
{
    ctx.Request.RouteValues.TryGetInt32("id", out var id);
    return id is > 0 and < 1000
        ? Results.Ok(new User(id, $"user-{id}"), SampleJson.Default.User)
        : Results.NotFound();
});

app.OnGet("/files/{*path}", ctx =>
    Results.Text($"catch-all captured: {ctx.Request.RouteValues["path"]}"));

// Proves the scope really is per request: both resolutions inside one request are the same
// instance, and a second request gets a different one.
app.OnGet("/scope", ctx =>
{
    var a = ctx.GetRequiredService<RequestId>();
    var b = ctx.GetRequiredService<RequestId>();
    return ctx.Response.WriteAsync($"same-instance={ReferenceEquals(a, b)} id={a.Value}");
});

app.OnPost("/echo", async ctx =>
{
    var body = await ctx.Request.ReadBodyAsStringAsync();
    await ctx.Response.WriteAsync($"echo:{body}");
});

app.OnGet("/boom", _ => throw new InvalidOperationException("deliberate failure"));

// --- A directory, as a drive ---
//
// One call maps the twenty-two routes of an RFC 4918 class 1 & 2 WebDAV mount. Point Finder (Go →
// Connect to Server) or Explorer (Map network drive) at http://localhost:8080/dav and the folder
// below opens as a drive — no client to write, and no client to install.
//
// A browser GET of the same URL shows a plain HTML index, which is the quickest way to see it
// working without mounting anything.
var davRoot = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "dav-root"));

if (!File.Exists(Path.Combine(davRoot.FullName, "readme.txt")))
    File.WriteAllText(Path.Combine(davRoot.FullName, "readme.txt"), "Edit me from your file manager.\n");

app.MapWebDav("/dav", o =>
{
    o.RootPath = davRoot.FullName;
    o.AllowWrite = true;
    o.AllowDelete = true;
    o.DisplayName = "Sample";
});

// Left open because this sample binds loopback and nothing else can reach it. A mount anyone else
// can see needs both of these — a WebDAV client sends its password on every single request:
//
//   app.MapWebDav("/dav", …).RequireAuthorization();     // or .RequireAuthorizationForChanges()
//   builder.Configure(o => o.Certificate = …);           // see /httpserver/tls/

// --- Controlling the server from inside the app ---
//
// The server is an ordinary object: hold onto it and start, stop or restart it whenever. This
// sample exposes it over HTTP for demonstration, but the same two calls sit behind a toggle in a
// MAUI app. With AddHttpServer(..., autoStart: false) the server is registered and configured but
// never listening until something asks it to.
app.OnGet("/server/status", ctx => ctx.Response.WriteAsync($"{app.State} {app.ListenUrl}"));

app.OnPost("/server/restart", async ctx =>
{
    // Not awaited inline: restarting tears down the connection this request arrived on.
    _ = Task.Run(async () =>
    {
        await Task.Delay(100);
        await app.RestartAsync();
    });

    await ctx.Response.WriteAsync("restarting");
});

app.StateChanged += (_, state) => Console.WriteLine($"  [server] {state}");

// --- Tier 0: everything no route claimed ---
app.OnRequest(ctx =>
{
    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    return ctx.Response.WriteAsync($"no route for {ctx.Request.Path}");
});

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await app.StartAsync(cts.Token);
Console.WriteLine($"Serving on {app.ListenUrl} — Ctrl+C to stop");

// --- Optional: also serve through a relay tunnel ---
//
//   dotnet run --project samples/Sample.Api -- --tunnel localhost:5050 --subdomain demo
//
// The tunnel dials out, so this works from behind NAT where nothing can connect in. Requests that
// arrive through it hit exactly the same routes as the local port above.
Task? tunnel = null;
if (Args.Value("--tunnel") is { } relayAddress)
{
    var (relayHost, relayPort) = relayAddress.Split(':') switch
    {
        [var h, var p] => (h, int.Parse(p)),
        [var h] => (h, 5050),
        _ => ("localhost", 5050)
    };

    var provider = new RelayTunnelProvider(
        new RelayTunnelOptions
        {
            Host = relayHost,
            Port = relayPort,
            Subdomain = Args.Value("--subdomain"),
            Token = Args.Value("--token") ?? "demo-token",
            UseTls = false
        },
        app.Services!.GetRequiredService<ILoggerFactory>().CreateLogger<RelayTunnelProvider>()
    );

    tunnel = app.RunTunnelAsync(provider, app.Services!.GetRequiredService<ILogger<Program>>(), cts.Token);
}

try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
}

if (tunnel is not null)
    await tunnel;

await app.StopAsync();

static class Args
{
    public static string? Value(string name)
    {
        var all = Environment.GetCommandLineArgs();
        for (var i = 0; i < all.Length - 1; i++)
        {
            if (string.Equals(all[i], name, StringComparison.OrdinalIgnoreCase))
                return all[i + 1];
        }
        return null;
    }
}

interface IGreeter
{
    string Greet(string name);
}

sealed class Greeter : IGreeter
{
    public string Greet(string name) => $"Hello, {name}!";
}

/// <summary>Scoped: one instance per HTTP request/response exchange.</summary>
sealed class RequestId
{
    public Guid Value { get; } = Guid.NewGuid();
}

/// <summary>
/// The same thing the lambda above does, as a type — constructor-injected, unit-testable, and
/// registered like any other service.
/// </summary>
sealed class RequestTimingMiddleware(ILogger<RequestTimingMiddleware> logger) : IHttpMiddleware
{
    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var sw = Stopwatch.StartNew();
        await next(context);

        logger.LogInformation(
            "{Method} {Path} -> {Status} ({Elapsed}ms)",
            context.Request.Method,
            context.Request.Path,
            context.Response.StatusCode,
            sw.ElapsedMilliseconds
        );
    }
}

sealed class InMemoryWidgetStore : IWidgetStore
{
    readonly Dictionary<int, Widget> widgets = new()
    {
        [1] = new Widget(1, "bolt"),
        [2] = new Widget(2, "flange"),
        [3] = new Widget(3, "grommet")
    };

    int next = 4;

    public ValueTask<Widget?> FindAsync(int id, CancellationToken cancellationToken)
        => new(this.widgets.GetValueOrDefault(id));

    public ValueTask<IReadOnlyList<Widget>> ListAsync(int take, string? search, CancellationToken cancellationToken)
    {
        IEnumerable<Widget> query = this.widgets.Values;

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(w => w.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

        return new ValueTask<IReadOnlyList<Widget>>(query.Take(take).ToArray());
    }

    public ValueTask<Widget> AddAsync(string name, CancellationToken cancellationToken)
    {
        var widget = new Widget(this.next++, name);
        this.widgets[widget.Id] = widget;
        return new ValueTask<Widget>(widget);
    }

    public ValueTask<bool> RemoveAsync(int id, CancellationToken cancellationToken)
        => new(this.widgets.Remove(id));
}

/// <summary>
/// Stand-in for a real user store. Passwords are compared in plain text here because this is a
/// sample; anything real hashes them with a slow KDF.
/// </summary>
sealed class InMemoryUserDirectory : IUserDirectory
{
    readonly SampleUser[] users =
    [
        new("ada", "Ada Lovelace", "hunter2", ["admin", "user"]),
        new("linus", "Linus Torvalds", "penguin", ["user"])
    ];

    public SampleUser? Verify(string username, string password) => this.users.FirstOrDefault(
        u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase) && u.Password == password
    );
}

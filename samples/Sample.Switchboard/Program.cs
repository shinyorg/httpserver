using System.Net;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sample.Switchboard;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Switchboard;

// A chat server built on a switchboard. Run it, then run Sample.Switchboard.Client in a couple of
// terminals: `dotnet run --project samples/Sample.Switchboard.Client -- alice`.
//
//   POST /admin/announce?text=…        everyone gets a notice (the operator, from outside the switchboard)
//   POST /admin/hang-up?user=alice     hangs up on every line alice has open

var builder = HttpServer.CreateBuilder();
builder.Options.Address = IPAddress.Loopback;
builder.Options.Port = int.TryParse(Environment.GetEnvironmentVariable("SWITCHBOARD_PORT"), out var port) ? port : 5080;
builder.Services.AddLogging(l => l.AddSimpleConsole(o => o.SingleLine = true));
builder.Services.AddSingleton<RoomStore>();

builder.AddSwitchboard(o =>
{
    o.HeartbeatInterval = TimeSpan.FromSeconds(15);
    o.ResumeWindow = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

// Stand-in authentication so the sample has users without a login flow: whoever the client says it
// is, in X-User. Swap in AddAuthentication().AddJwtBearer(...) for anything real.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Headers.GetFirst("X-User") is { Length: > 0 } user)
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "sample"));

    await next(ctx);
});

app.MapSwitchboard<ChatBoard>("/chat");

app.MapPost("/admin/announce", async ctx =>
{
    var board = ctx.RequestServices.GetRequiredService<IOperator<ChatBoard, IChatClient>>();
    await board.Clients.All.Notice(ctx.Request.Query.GetFirst("text") ?? "(nothing)");
    ctx.Response.StatusCode = StatusCodes.Status204NoContent;
});

app.MapPost("/admin/hang-up", async ctx =>
{
    var board = ctx.RequestServices.GetRequiredService<IOperator<ChatBoard, IChatClient>>();
    var count = await board.Lines.HangUpUserAsync(ctx.Request.Query.GetFirst("user") ?? "", "Removed by an admin");
    await ctx.Response.WriteAsync($"hung up on {count} line(s)");
});

Console.WriteLine($"Chat switchboard on {app.Options.Address}:{app.Options.Port}/chat — Ctrl+C to stop");
await app.RunAsync();

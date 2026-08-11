using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Sample.Mcp;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Mcp;

// ---------------------------------------------------------------------------
// An MCP server, hosted on Shiny.Net.HttpServer rather than ASP.NET Core.
//
// The point of the exercise: this same file runs unchanged inside a .NET MAUI app, where
// ASP.NET Core cannot go. The tools below then have something worth exposing — the device the
// app is running on.
//
//   dotnet run --project samples/Sample.Mcp
//   npx @modelcontextprotocol/inspector          # then connect to http://localhost:8181/mcp
// ---------------------------------------------------------------------------

var builder = HttpServer.CreateBuilder();
builder.Configure(o =>
{
    o.Port = 8181;
    o.HideExceptionDetails = false;
});

builder.Services.AddLogging(l => l.AddSimpleConsole(c => c.SingleLine = true).SetMinimumLevel(LogLevel.Information));

// State the tools mutate, to make the difference between a session and a stateless call visible.
builder.Services.AddSingleton<Thermostat>();

builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new Implementation { Name = "sample-thermostat", Version = "1.0.0" };
        o.ServerInstructions = "Reads and adjusts a thermostat. Temperatures are in Celsius.";
    })
    .WithTools<ThermostatTools>()
    .WithHttpTransport(o =>
    {
        // Nothing else is allowed to reach this from a browser. Without an allow-list, any page the
        // user happens to have open could drive a server bound to their own machine.
        o.AllowedOrigins.Add("http://localhost:6274");   // the MCP Inspector's dev server
        o.IdleSessionTimeout = TimeSpan.FromMinutes(10);
    });

var app = builder.Build();

app.MapMcp();

// The MCP endpoint is just another route: everything else the server does still works alongside it.
app.OnGet("/health", ctx => ctx.Response.WriteAsync("ok"));

Console.WriteLine("MCP endpoint:  http://localhost:8181/mcp");
Console.WriteLine("Press Ctrl+C to stop.");

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.Cancel();
};

await app.RunAsync(stopping.Token);

namespace Sample.Mcp
{
    /// <summary>The thing being exposed. In a real app this is the device, not a field.</summary>
    public sealed class Thermostat
    {
        public double Target { get; set; } = 20.5;

        public double Current => this.Target - 0.4;
    }

    /// <summary>
    /// Tools are ordinary methods. Constructor and parameter injection work exactly as they do in
    /// an endpoint class, because the MCP server is created from the HTTP server's own container.
    /// </summary>
    [McpServerToolType]
    public sealed class ThermostatTools
    {
        [McpServerTool(Name = "get_temperature"), Description("Reads the current and target temperature in Celsius.")]
        public static string GetTemperature(Thermostat thermostat)
            => $"Currently {thermostat.Current:0.0}°C, set to {thermostat.Target:0.0}°C.";

        [McpServerTool(Name = "set_temperature"), Description("Sets the target temperature in Celsius.")]
        public static string SetTemperature(
            Thermostat thermostat,
            [Description("Target temperature, between 5 and 30 degrees Celsius.")] double celsius
        )
        {
            if (celsius is < 5 or > 30)
                return "Refused: pick something between 5 and 30°C.";

            thermostat.Target = celsius;
            return $"Target set to {celsius:0.0}°C.";
        }
    }
}

using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer.Tunneling;

// ---------------------------------------------------------------------------
// The public end of a tunnel.
//
// Two ports: clients register on the control port, the world arrives on the
// public port, and requests are routed to a tunnel by their Host header.
//
//   dotnet run --project samples/Sample.Relay
//   dotnet run --project samples/Sample.Api -- --tunnel localhost:5050 --subdomain demo
//   curl -H 'Host: demo.localhost' http://127.0.0.1:8090/ping
//
// TLS is off and the token is fixed here because this is a local demo. A relay
// on the public internet wants ControlHttps set — registration carries the
// token — and a token that is not in a source file.
// ---------------------------------------------------------------------------

using var loggerFactory = LoggerFactory.Create(builder => builder
    .AddSimpleConsole(c => c.SingleLine = true)
    .SetMinimumLevel(LogLevel.Information));

var relay = new RelayServer(
    new RelayServerOptions
    {
        ControlPort = 5050,
        PublicPort = 8090,
        Domain = "localhost",
        Token = "demo-token"

        // ControlHttps / PublicHttps deliberately left null: this is a loopback demo.
    },
    loggerFactory
);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await relay.StartAsync(cts.Token);

Console.WriteLine($"Control : {relay.ControlUrl}   (tunnel clients register here)");
Console.WriteLine($"Public  : {relay.PublicUrl}   (routed by Host header)");
Console.WriteLine("Ctrl+C to stop");

try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
}

await relay.StopAsync();

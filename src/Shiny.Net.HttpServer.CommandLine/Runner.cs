using System.ComponentModel;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer.Ssh;

namespace Shiny.Net.HttpServer.CommandLine;


public static class Runner
{
    public static async Task<int> RunAsync(ServeSettings settings, CancellationToken cancellationToken)
    {
        if (settings.Validate() is { } invalid)
        {
            Error(invalid);
            return 1;
        }

        // A dashboard needs a keyboard and a screen. Piped into a file or run from a script it would
        // be neither, and the banner is what that caller wants anyway.
        if (settings.UseTui && !Console.IsInputRedirected && !Console.IsOutputRedirected)
            return await Tui.Dashboard.RunAsync(settings, cancellationToken).ConfigureAwait(false);

        return await RunPlainAsync(settings, cancellationToken).ConfigureAwait(false);
    }


    static async Task<int> RunPlainAsync(ServeSettings settings, CancellationToken cancellationToken)
    {
        var prefix = settings.UrlPrefix;
        using var certificate = settings.UseHttps ? ServerFactory.CreateCertificate() : null;

        await using var server = ServerFactory.Build(
            settings,
            certificate,
            x => x
                .AddSimpleConsole(o =>
                {
                    o.SingleLine = true;
                    o.TimestampFormat = "HH:mm:ss ";
                })
                .SetMinimumLevel(settings.Verbose ? LogLevel.Debug : LogLevel.Warning)

                // The tunnel warns that it is trusting an unverified host key, which is exactly what a
                // quick tunnel does by design - and the banner says so in plainer words a few lines
                // later. Left in for --verbose, kept out of a normal run's first three lines.
                .AddFilter("Shiny.Net.HttpServer.Ssh", settings.Verbose ? LogLevel.Debug : LogLevel.Error),
            middleware: settings.Verbose ? LogRequestAsync : null
        );

        // The tunnel hands connections straight to ServeAsync, so it is up before - and independent
        // of - the listener. That is what makes "--tunnel -a localhost" a real combination: nothing
        // on the LAN, everything through the tunnel.
        await using var tunnel = settings.UseTunnel
            ? QuickTunnel.For(
                server,
                QuickTunnelHost.Pinggy,
                settings.TunnelToken,
                loggerFactory: server.Services?.GetService<ILoggerFactory>()
            )
            : null;

        var tunnelUrl = tunnel is null ? null : await OpenTunnelAsync(tunnel, cancellationToken).ConfigureAwait(false);

        PrintBanner(settings, prefix, tunnelUrl);

        // The address changes on every reconnect, which kills whatever is already on screen - so a
        // new one is announced, with its own code, rather than leaving a dead link as the last word.
        if (tunnel is not null)
            tunnel.PropertyChanged += (_, e) => OnTunnelUrlChanged(settings, prefix, tunnel, e);

        try
        {
            await server.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException ex)
        {
            Error($"Cannot listen on {settings.Address}:{settings.Port} - {ex.Message}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("stopped");
        return 0;
    }


    /// <summary>
    /// Brings the tunnel up, or explains why there is none. A tunnel that will not open is not a
    /// reason to refuse to serve: the directory is still on this network, and the banner still has
    /// somewhere to point.
    /// </summary>
    static async Task<string?> OpenTunnelAsync(QuickTunnel tunnel, CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("  opening tunnel...");

        try
        {
            var url = await tunnel.StartAsync(cancellationToken).ConfigureAwait(false);
            if (url is { Length: > 0 })
                return url;

            Error(tunnel.LastError ?? "The tunnel connected but never reported an address.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Error($"The tunnel could not be opened - {ex.Message}");
        }

        Warn("Serving on this network only.");
        return null;
    }


    static void OnTunnelUrlChanged(ServeSettings settings, string prefix, QuickTunnel tunnel, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QuickTunnel.PublicUrl) || tunnel.PublicUrl is not { Length: > 0 } url)
            return;

        Console.WriteLine();
        Warn("The tunnel reconnected on a new address. The previous one no longer answers.");
        Line("Tunnel", ServerUrls.Tunnel(url, prefix));

        if (settings.ShowQr)
        {
            Console.WriteLine();
            PrintQr(ServerUrls.Tunnel(url, prefix));
        }
        Console.WriteLine();
    }


    static async ValueTask LogRequestAsync(HttpContext context, RequestDelegate next)
    {
        var started = Environment.TickCount64;
        await next(context).ConfigureAwait(false);

        Console.WriteLine(
            $"  {context.Request.Method,-6} {context.Request.Path}  {context.Response.StatusCode}  {Environment.TickCount64 - started}ms"
        );
    }


    static void PrintBanner(ServeSettings settings, string prefix, string? tunnelUrl)
    {
        Console.WriteLine();
        Console.WriteLine("shinyhttpserver");
        Console.WriteLine();
        Line("Directory", settings.RootPath);

        foreach (var url in ServerUrls.All(settings))
            Line("URL", url);

        if (tunnelUrl is not null)
            Line("Tunnel", ServerUrls.Tunnel(tunnelUrl, prefix));

        Line("Operations", settings.Permissions.Describe());
        Line("Mount", "WebDAV - Finder, Explorer and any WebDAV client can open the URL as a drive");
        Line(
            "Auth",
            settings.AuthEnabled
                ? $"basic ({String.Join(", ", settings.Users.Select(x => x.Username))}){(settings.AuthChangesOnly ? ", changes only" : "")}"
                : "none"
        );

        if (settings.Permissions.AllowsChanges())
            Line("Max upload", Size(settings.MaxUploadBytes));

        Console.WriteLine();

        // Said before the write warning, because it is what turns that warning from "the office
        // network" into "the internet".
        if (tunnelUrl is not null)
        {
            Warn(
                "The tunnel is public: anyone holding the address can reach this directory, and the traffic passes through pinggy.io."
                + (settings.TunnelToken is { Length: > 0 } ? "" : " An anonymous tunnel stops after 60 minutes.")
            );
        }

        if (settings.Permissions.AllowsChanges() && !settings.AuthEnabled)
        {
            Warn(
                tunnelUrl is null
                    ? "Writes are open to anyone who can reach this server. Add --user name:password."
                    : "Writes are open to anyone on the internet holding the tunnel address. Add --user name:password."
            );
        }

        if (settings.UseHttps)
            Warn("The certificate is self-signed and generated at startup, so clients will not trust it.");

        // The tunnel address is the one worth scanning when there is one: it reaches a phone that
        // is not on this network at all, which the LAN address does not.
        if (settings.ShowQr)
            PrintQr(tunnelUrl is null ? ServerUrls.Shareable(settings) : ServerUrls.Tunnel(tunnelUrl, prefix));

        Console.WriteLine("Ctrl+C to stop");
        Console.WriteLine();
    }


    /// <summary>
    /// The point of the code is a phone that is not this machine, so it carries the address another
    /// device can reach - and nothing at all when there is no such address.
    /// </summary>
    static void PrintQr(string? url)
    {
        if (url == null || !QrCode.TryEncode(url, out var code))
            return;

        // half-blocks and colour are a terminal's, not a log file's, and a code that wraps is a code
        // that will not scan
        if (Console.IsOutputRedirected || ConsoleWidth() < QrConsole.Width(code) + 2)
            return;

        QrConsole.Write(code, "  ");
        Console.WriteLine();
        Line("Scan", url);
        Console.WriteLine();
    }


    /// <summary>The window's width, or no limit at all when there is no window to ask.</summary>
    static int ConsoleWidth()
    {
        try
        {
            var width = Console.WindowWidth;
            return width > 0 ? width : Int32.MaxValue;
        }
        catch (IOException)
        {
            return Int32.MaxValue;
        }
    }


    static string Size(long bytes)
        => bytes switch
        {
            >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.##} GB",
            >= 1024L * 1024 => $"{bytes / 1024d / 1024:0.##} MB",
            >= 1024 => $"{bytes / 1024d:0.##} KB",
            _ => $"{bytes} bytes"
        };


    static void Line(string label, string value)
        => Console.WriteLine($"  {label,-11} {value}");


    static void Warn(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"! {message}");
        Console.ResetColor();
        Console.WriteLine();
    }


    static void Error(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine(message);
        Console.ResetColor();
    }
}

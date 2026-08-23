using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer.Security;
using Shiny.Net.HttpServer.Ssh;
using Shiny.Net.HttpServer.WebDav;

namespace Shiny.Net.HttpServer.CommandLine;


public static class Runner
{
    public static async Task<int> RunAsync(ServeSettings settings, CancellationToken cancellationToken)
    {
        if (!Validate(settings, out var prefix))
            return 1;

        var certificate = settings.UseHttps
            ? ServerCertificate.Create(o => o.CommonName = "shinyhttpserver")
            : null;

        var builder = HttpServer.CreateBuilder();
        builder.Services.AddLogging(x => x
            .AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            })
            .SetMinimumLevel(settings.Verbose ? LogLevel.Debug : LogLevel.Warning)

            // The tunnel warns that it is trusting an unverified host key, which is exactly what a
            // quick tunnel does by design - and the banner says so in plainer words a few lines
            // later. Left in for --verbose, kept out of a normal run's first three lines.
            .AddFilter("Shiny.Net.HttpServer.Ssh", settings.Verbose ? LogLevel.Debug : LogLevel.Error)
        );

        builder.Configure(o =>
        {
            if (certificate == null)
                o.Listen(settings.Address, settings.Port);
            else
                o.ListenHttps(settings.Address, settings.Port, certificate);

            o.ServerHeader = "shinyhttpserver";

            if (settings.Permissions.Has(Permissions.Create) || settings.Permissions.Has(Permissions.Update))
                o.Limits.MaxRequestBodySize = settings.MaxUploadBytes;
        });

        if (settings.AuthEnabled)
        {
            builder
                .AddAuthentication()
                .AddBasic(o =>
                {
                    o.Realm = settings.Realm;
                    o.AllowInsecureTransport = settings.AllowInsecureAuth;

                    foreach (var user in settings.Users)
                        o.AddUser(user.Username, user.Password);
                });

            builder.AddAuthorization(o => o.SetDefaultPolicy(p => p.RequireAuthenticatedUser()));
        }

        await using var server = builder.Build();

        if (settings.AuthEnabled)
        {
            server.UseAuthentication();
            server.UseAuthorization();
        }

        if (settings.Verbose)
            server.Use(LogRequestAsync);

        // the mount has one write flag, so create/update only differ if something checks first
        if (NeedsWriteGuard(settings.Permissions))
            server.Use(new WriteGuard(prefix, settings.RootPath, settings.Permissions));

        // WebDAV rather than the JSON file browser, because it is two things at once: the file
        // manager a browser gets on GET, and a drive Finder, Explorer and the Linux file managers
        // can mount at the same address - which is the shortest path from "a directory on this
        // machine" to "a folder on that one".
        var mount = server.MapWebDav(prefix, o =>
        {
            o.RootPath = settings.RootPath;
            o.AllowWrite = settings.Permissions.Has(Permissions.Create) || settings.Permissions.Has(Permissions.Update);
            o.AllowDelete = settings.Permissions.Has(Permissions.Delete);
            o.ServeHiddenFiles = settings.ServeHidden;
            o.MaxUploadBytes = settings.MaxUploadBytes;
            // The directory's own name, which is what the manager's breadcrumb and a mounted drive
            // are labelled with. A root directory has no name, and there the mount's own default
            // is the better answer than an empty label.
            if (Path.GetFileName(Path.TrimEndingDirectorySeparator(settings.RootPath)) is { Length: > 0 } name)
                o.DisplayName = name;
        });

        if (settings.AuthEnabled)
        {
            if (settings.AuthChangesOnly)
                mount.RequireAuthorizationForChanges();
            else
                mount.RequireAuthorization();
        }

        // mounted anywhere else the site root is a 404, which is a worse answer than the listing
        if (prefix != "/")
        {
            server.MapGet("/", ctx =>
            {
                ctx.Response.Redirect(prefix);
                return ValueTask.CompletedTask;
            });
        }

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
        Line("Tunnel", TunnelUrl(url, prefix));

        if (settings.ShowQr)
        {
            Console.WriteLine();
            PrintQr(TunnelUrl(url, prefix));
        }
        Console.WriteLine();
    }


    /// <summary>The tunnel terminates at the site root, so the mount point has to be put back on.</summary>
    internal static string TunnelUrl(string url, string prefix)
        => prefix == "/" ? url.TrimEnd('/') + "/" : url.TrimEnd('/') + prefix;


    static bool NeedsWriteGuard(Permissions permissions)
    {
        var create = permissions.Has(Permissions.Create);
        var update = permissions.Has(Permissions.Update);

        return (create || update) && !(create && update);
    }


    /// <summary>Everything that should stop the server before it opens a socket.</summary>
    static bool Validate(ServeSettings settings, out string prefix)
    {
        prefix = settings.UrlPrefix;

        if (!Directory.Exists(settings.RootPath))
        {
            Error($"'{settings.RootPath}' is not a directory.");
            return false;
        }

        if (settings.Port is < 1 or > 65535)
        {
            Error($"{settings.Port} is not a port. Use 1-65535.");
            return false;
        }

        // basic auth is the password itself on every request, so plain HTTP off-box is refused
        if (settings.AuthEnabled && !settings.UseHttps && !settings.IsLoopbackOnly && !settings.AllowInsecureAuth)
        {
            Error(
                $"""
                 Basic auth over plain HTTP on {settings.Address} would send the password across the network in the clear on every request.

                 Pick one:
                   --https                  serve over TLS with a self-signed certificate
                   --tunnel -a localhost    reach it only through the tunnel, which is encrypted
                   --address localhost      keep the server on this machine
                   --allow-insecure-auth    send it anyway
                 """
            );
            return false;
        }
        return true;
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

        foreach (var url in Urls(settings, prefix))
            Line("URL", url);

        if (tunnelUrl is not null)
            Line("Tunnel", TunnelUrl(tunnelUrl, prefix));

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
            PrintQr(tunnelUrl is null ? ShareableUrl(settings, prefix) : TunnelUrl(tunnelUrl, prefix));

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


    static string? ShareableUrl(ServeSettings settings, string prefix)
    {
        if (!settings.Address.Equals(IPAddress.Any) && !settings.Address.Equals(IPAddress.IPv6Any))
            return IPAddress.IsLoopback(settings.Address) ? null : Url(settings, Host(settings.Address), prefix);

        var address = LocalAddresses().FirstOrDefault();
        return address == null ? null : Url(settings, Host(address), prefix);
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


    static IEnumerable<string> Urls(ServeSettings settings, string prefix)
    {
        if (!settings.Address.Equals(IPAddress.Any) && !settings.Address.Equals(IPAddress.IPv6Any))
        {
            yield return Url(settings, Host(settings.Address), prefix);
            yield break;
        }

        yield return Url(settings, "localhost", prefix);

        foreach (var address in LocalAddresses())
            yield return Url(settings, Host(address), prefix);
    }


    static string Url(ServeSettings settings, string host, string prefix)
        => $"{settings.Scheme}://{host}:{settings.Port}{prefix}";


    static IEnumerable<IPAddress> LocalAddresses()
        => NetworkInterface
            .GetAllNetworkInterfaces()
            .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(x => x.GetIPProperties().UnicastAddresses)
            .Select(x => x.Address)
            .Where(x => x.AddressFamily == AddressFamily.InterNetwork);


    static string Host(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();


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

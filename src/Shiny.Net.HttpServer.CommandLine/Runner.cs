using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer.FileBrowser;
using Shiny.Net.HttpServer.Security;

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
            builder.Services
                .AddAuthentication()
                .AddBasic(o =>
                {
                    o.Realm = settings.Realm;
                    o.AllowInsecureTransport = settings.AllowInsecureAuth;

                    foreach (var user in settings.Users)
                        o.AddUser(user.Username, user.Password);
                });

            builder.Services.AddAuthorization(o => o.SetDefaultPolicy(p => p.RequireAuthenticatedUser()));
        }

        await using var server = builder.Build();

        if (settings.AuthEnabled)
        {
            server.UseAuthentication();
            server.UseAuthorization();
        }

        if (settings.Verbose)
            server.Use(LogRequestAsync);

        // the browser has one write flag, so create/update only differ if something checks first
        if (NeedsWriteGuard(settings.Permissions))
            server.Use(new WriteGuard(prefix, settings.RootPath, settings.Permissions));

        var endpoints = server.MapFileBrowser(prefix, o =>
        {
            o.RootPath = settings.RootPath;
            o.AllowWrite = settings.Permissions.Has(Permissions.Create) || settings.Permissions.Has(Permissions.Update);
            o.AllowCreateDirectories = settings.Permissions.Has(Permissions.Create);
            o.AllowDelete = settings.Permissions.Has(Permissions.Delete);
            o.ServeHiddenFiles = settings.ServeHidden;
            o.MaxUploadBytes = settings.MaxUploadBytes;
        });

        if (settings.AuthEnabled)
        {
            if (settings.AuthChangesOnly)
                endpoints.RequireAuthorizationForChanges();
            else
                endpoints.RequireAuthorization();
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

        PrintBanner(settings, prefix);

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


    static void PrintBanner(ServeSettings settings, string prefix)
    {
        Console.WriteLine();
        Console.WriteLine("shinyhttpserver");
        Console.WriteLine();
        Line("Directory", settings.RootPath);

        foreach (var url in Urls(settings, prefix))
            Line("URL", url);

        Line("Operations", settings.Permissions.Describe());
        Line(
            "Auth",
            settings.AuthEnabled
                ? $"basic ({String.Join(", ", settings.Users.Select(x => x.Username))}){(settings.AuthChangesOnly ? ", changes only" : "")}"
                : "none"
        );

        if (settings.Permissions.AllowsChanges())
            Line("Max upload", Size(settings.MaxUploadBytes));

        Console.WriteLine();

        if (settings.Permissions.AllowsChanges() && !settings.AuthEnabled)
            Warn("Writes are open to anyone who can reach this server. Add --user name:password.");

        if (settings.UseHttps)
            Warn("The certificate is self-signed and generated at startup, so clients will not trust it.");

        Console.WriteLine("Ctrl+C to stop");
        Console.WriteLine();
    }


    static IEnumerable<string> Urls(ServeSettings settings, string prefix)
    {
        if (!settings.Address.Equals(IPAddress.Any) && !settings.Address.Equals(IPAddress.IPv6Any))
        {
            yield return $"{settings.Scheme}://{Host(settings.Address)}:{settings.Port}{prefix}";
            yield break;
        }

        yield return $"{settings.Scheme}://localhost:{settings.Port}{prefix}";

        foreach (var address in LocalAddresses())
            yield return $"{settings.Scheme}://{Host(address)}:{settings.Port}{prefix}";
    }


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

using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer.CommandLine.Monitoring;
using Shiny.Net.HttpServer.Security;
using Shiny.Net.HttpServer.WebDav;

namespace Shiny.Net.HttpServer.CommandLine;


/// <summary>
/// Turns settings into a composed, not-yet-started server. Shared by the plain console run and the
/// dashboard, so the two cannot drift into serving the same flags differently.
/// </summary>
public static class ServerFactory
{
    /// <param name="settings">What to serve, and how. Validated by the caller.</param>
    /// <param name="certificate">Required when <see cref="ServeSettings.UseHttps"/> is set.</param>
    /// <param name="logging">Where the server's own log goes - the console, or the dashboard's log tab.</param>
    /// <param name="monitor">Sees every request first, ahead of authentication, when given.</param>
    /// <param name="middleware">Anything else that should run ahead of authentication - the console's request log.</param>
    public static HttpServer Build(
        ServeSettings settings,
        X509Certificate2? certificate,
        Action<ILoggingBuilder> logging,
        TrafficMonitor? monitor = null,
        Func<HttpContext, RequestDelegate, ValueTask>? middleware = null
    )
    {
        if (settings.UseHttps && certificate is null)
            throw new ArgumentException("HTTPS needs a certificate.", nameof(certificate));

        var prefix = settings.UrlPrefix;
        var builder = HttpServer.CreateBuilder();
        builder.Services.AddLogging(logging);

        builder.Configure(o =>
        {
            if (settings.UseHttps)
                o.ListenHttps(settings.Address, settings.Port, certificate!);
            else
                o.Listen(settings.Address, settings.Port);

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

        var server = builder.Build();

        if (monitor is not null)
            server.Use(monitor);

        if (middleware is not null)
            server.Use(middleware);

        if (settings.AuthEnabled)
        {
            server.UseAuthentication();
            server.UseAuthorization();
        }

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

        return server;
    }


    public static X509Certificate2 CreateCertificate()
        => ServerCertificate.Create(o => o.CommonName = "shinyhttpserver");


    static bool NeedsWriteGuard(Permissions permissions)
    {
        var create = permissions.Has(Permissions.Create);
        var update = permissions.Has(Permissions.Update);

        return (create || update) && !(create && update);
    }
}

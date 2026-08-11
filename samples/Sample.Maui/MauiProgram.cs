using System.Net;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Sample.Maui.Server;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.FileBrowser;
using Shiny.Net.HttpServer.Mcp;
using Shiny.Net.HttpServer.Security;
using Shiny.Net.HttpServer.Ssh;
using Shiny.Net.HttpServer.StaticFiles;

namespace Sample.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()

            // Shiny.Maui.Shell owns the tabs: AddGeneratedMaps() is source-generated from the
            // [ShellMap] attributes on the view models, so every page and view model is registered
            // — and every route named — without a list here that drifts from the attributes.
            .UseShinyShell(shell => shell.AddGeneratedMaps())

            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        // The app's own JSON metadata, registered once. A source generator cannot see another
        // generator's output, so this is the app's job rather than the library's — and it is what
        // keeps serialization reflection-free, which iOS requires.
        JsonTypeInfoRegistry.Register(ApiJsonContext.Default);

        builder.Services.AddSingleton<NoteStore>();
        builder.Services.AddSingleton<RequestLog>();
        builder.Services.AddSingleton<RequireAuthenticationMiddleware>();

        // The badge on the Traffic tab has to keep counting while a different tab is on screen, so
        // it is a service with a startup hook rather than anything the Traffic view model owns.
        builder.Services.AddSingleton<TrafficBadge>();
        builder.Services.AddSingleton<IMauiInitializeService>(sp => sp.GetRequiredService<TrafficBadge>());

        // The accounts live in the app's own storage and change from the settings screen, so the
        // validator is a service rather than a list handed over at startup.
        builder.Services.AddAuthentication().AddBasic<CredentialStore>(o => o.Realm = "Shiny device");

        // An MCP server, in a phone app. The MCP SDK's own HTTP transport is an ASP.NET Core package,
        // so this is the part that cannot be done any other way.
        //
        // ApiJsonContext is handed to WithTools rather than left to reflection: a tool's parameter
        // and return types are published to the client as a JSON schema, and building that schema by
        // reflection does not survive trimming — which on iOS is not optional. The context already
        // describes DeviceSummary and Note for the HTTP API, so the tools reuse it. Miss a type and
        // MapMcp() says so at startup rather than on the first request.
        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "shiny-device", Version = "1.0.0" };
                o.ServerInstructions =
                    "Reads and edits the notes held by a phone running the Shiny HTTP Server sample, " +
                    "and reports what that device is.";
            })
            .WithTools<DeviceTools>(ApiJsonContext.Default.Options)
            .WithHttpTransport(o =>
            {
                // A phone is not a datacentre, and this endpoint is reachable from the public
                // internet whenever the tunnel is up.
                o.MaxSessions = 8;
                o.IdleSessionTimeout = TimeSpan.FromMinutes(10);

                // AllowedOrigins is left empty, which refuses every browser origin. Native MCP
                // clients send no Origin header and are unaffected; what this stops is a page the
                // user happens to have open driving the server behind their back.
            });

        builder.Services.AddHttpServer(
            options =>
            {
                // Any, not loopback: the point is for another device to reach this one. Port 0
                // lets the OS pick, so two copies of the app on one network do not collide.
                options.Address = IPAddress.Any;
                options.Port = 0;
            },
            server =>
            {
                var services = server.Services!;

                // First in the pipeline, ahead of authentication, so the Traffic tab shows the
                // requests that were turned away as well as the ones that were answered.
                server.Use(services.GetRequiredService<RequestLog>());

                // Identify the caller, then insist on one. Both run before the static file handler,
                // because that handler answers before routing and would otherwise serve the page to
                // anyone with the link.
                server.UseAuthentication();
                server.Use(services.GetRequiredService<RequireAuthenticationMiddleware>());

                // The page a visitor lands on, served straight out of the assembly — a packaged app
                // has no wwwroot on disk to point at.
                server.UseEmbeddedFiles(typeof(MauiProgram).Assembly, "Sample.Maui.wwwroot");

                DeviceApi.Map(server, services.GetRequiredService<NoteStore>());

                // Deliberately not added to RequireAuthenticationMiddleware.AllowAnonymous: this
                // endpoint hands out the device's state and lets a caller write to it, so it sits
                // behind the same password as everything else. An MCP client reaches it by sending
                // the Basic credentials from the settings screen.
                server.MapMcp();

                // The app's own storage, over HTTP. Mapped as routes rather than middleware so
                // authorization can differ per verb — here everything is already behind the password
                // gate, so this asks for nothing further.
                server.MapFileBrowser("/files", o =>
                {
                    o.RootPath = FileSystem.AppDataDirectory;
                    o.AllowWrite = true;
                    o.AllowDelete = true;
                });
            },

            // Started by the UI instead, so the app does not open a port before anyone asked it to.
            autoStart: false
        );

        // localhost.run: no account, no key, nothing installed. The URL it assigns is what the app
        // shows on screen.
        builder.Services.AddQuickTunnel();

        return builder.Build();
    }
}

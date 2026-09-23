using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net;

namespace Shiny.Net.HttpServer.CommandLine;


/// <summary>
/// The command line surface. Everything the server needs is decided here so that
/// <see cref="Runner"/> only ever deals with a validated <see cref="ServeSettings"/>.
/// </summary>
public static class Cli
{
    public static RootCommand Build(Func<ServeSettings, CancellationToken, Task<int>> run)
    {
        var pathArg = new Argument<string>("path")
        {
            Description = "Directory to serve. Defaults to the current directory.",
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => "."
        };

        var portOpt = new Option<int>("--port", "-p")
        {
            Description = "Port to listen on.",
            DefaultValueFactory = _ => 8080
        };

        var addressOpt = new Option<IPAddress>("--address", "-a")
        {
            Description = "Address to bind: an IP, 'any' (all interfaces) or 'localhost'. Defaults to every interface so other devices can reach it.",
            HelpName = "address",
            DefaultValueFactory = _ => IPAddress.Any,
            CustomParser = ParseAddress
        };

        var prefixOpt = new Option<string>("--prefix")
        {
            Description = "URL prefix the directory is mounted at.",
            DefaultValueFactory = _ => "/"
        };

        var allowOpt = new Option<Permissions>("--allow", "-m")
        {
            Description = "Operations to allow: read, create, update, delete, all. Repeatable or comma separated. Read is always allowed.",
            HelpName = "read|create|update|delete|all",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => Permissions.Read,
            CustomParser = ParsePermissions
        };

        var userOpt = new Option<BasicUser[]>("--user", "-u")
        {
            Description = "Enables basic auth with user:password. Repeat for more than one user.",
            HelpName = "user:password",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => [],
            CustomParser = ParseUsers
        };

        var realmOpt = new Option<string>("--realm")
        {
            Description = "Basic auth realm shown in the browser prompt.",
            DefaultValueFactory = _ => "shinyhttpserver"
        };

        var authChangesOpt = new Option<bool>("--auth-changes-only")
        {
            Description = "Leaves reads open and only requires a login for create/update/delete."
        };

        var insecureAuthOpt = new Option<bool>("--allow-insecure-auth")
        {
            Description = "Allows basic auth over unencrypted, non-loopback connections. The password crosses the network in the clear on every request."
        };

        var httpsOpt = new Option<bool>("--https")
        {
            Description = "Serves over HTTPS with a self-signed certificate generated at startup. Clients will warn about it."
        };

        var tunnelOpt = new Option<bool>("--tunnel")
        {
            Description = "Opens a public pinggy.io tunnel and shares that address instead of the LAN one. Anonymous tunnels stop after 60 minutes."
        };

        var tunnelTokenOpt = new Option<string?>("--tunnel-token")
        {
            Description = "A pinggy.io access token, which lifts the 60 minute cap an anonymous tunnel has. Implies --tunnel.",
            HelpName = "token"
        };

        var hiddenOpt = new Option<bool>("--hidden")
        {
            Description = "Includes dotfiles and hidden files in listings and downloads."
        };

        var maxUploadOpt = new Option<long>("--max-upload")
        {
            Description = "Largest accepted upload, e.g. 500k, 64mb, 2gb.",
            HelpName = "size",
            DefaultValueFactory = _ => SettingParsers.DefaultMaxUpload,
            CustomParser = ParseSize
        };

        var noQrOpt = new Option<bool>("--no-qr")
        {
            Description = "Leaves the QR code out of the banner."
        };

        var verboseOpt = new Option<bool>("--verbose", "-v")
        {
            Description = "Logs every request."
        };

        var noTuiOpt = new Option<bool>("--no-tui")
        {
            Description = "Prints the banner and a plain log instead of opening the dashboard. The dashboard is also skipped whenever input or output is redirected."
        };

        var root = new RootCommand("Serves a directory over HTTP: a file manager in a browser, and a WebDAV drive in Finder or Explorer.")
        {
            pathArg,
            portOpt,
            addressOpt,
            prefixOpt,
            allowOpt,
            userOpt,
            realmOpt,
            authChangesOpt,
            insecureAuthOpt,
            httpsOpt,
            tunnelOpt,
            tunnelTokenOpt,
            hiddenOpt,
            maxUploadOpt,
            noQrOpt,
            verboseOpt,
            noTuiOpt
        };

        root.SetAction((parseResult, ct) =>
        {
            var settings = new ServeSettings
            {
                RootPath = Path.GetFullPath(parseResult.GetRequiredValue(pathArg)),
                Address = parseResult.GetRequiredValue(addressOpt),
                Port = parseResult.GetRequiredValue(portOpt),
                UrlPrefix = SettingParsers.NormalizePrefix(parseResult.GetRequiredValue(prefixOpt)),
                Permissions = parseResult.GetRequiredValue(allowOpt) | Permissions.Read,
                Users = parseResult.GetRequiredValue(userOpt),
                Realm = parseResult.GetRequiredValue(realmOpt),
                AuthChangesOnly = parseResult.GetValue(authChangesOpt),
                AllowInsecureAuth = parseResult.GetValue(insecureAuthOpt),
                UseHttps = parseResult.GetValue(httpsOpt),
                UseTunnel = parseResult.GetValue(tunnelOpt) || parseResult.GetValue(tunnelTokenOpt) is { Length: > 0 },
                TunnelToken = parseResult.GetValue(tunnelTokenOpt),
                ServeHidden = parseResult.GetValue(hiddenOpt),
                MaxUploadBytes = parseResult.GetRequiredValue(maxUploadOpt),
                ShowQr = !parseResult.GetValue(noQrOpt),
                Verbose = parseResult.GetValue(verboseOpt),
                UseTui = !parseResult.GetValue(noTuiOpt)
            };
            return run(settings, ct);
        });
        return root;
    }


    static IPAddress ParseAddress(ArgumentResult result)
    {
        if (!SettingParsers.TryParseAddress(result.Tokens[0].Value, out var address, out var error))
            result.AddError(error!);

        return address;
    }


    static Permissions ParsePermissions(ArgumentResult result)
    {
        var permissions = Permissions.Read;

        foreach (var token in result.Tokens)
        {
            foreach (var raw in token.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!SettingParsers.TryAddPermission(raw, ref permissions, out var error))
                    result.AddError(error!);
            }
        }
        return permissions;
    }


    static BasicUser[] ParseUsers(ArgumentResult result)
    {
        var users = new List<BasicUser>();

        foreach (var token in result.Tokens)
        {
            if (SettingParsers.TryParseUser(token.Value, out var user, out var error))
                users.Add(user!);
            else
                result.AddError(error!);
        }
        return users.ToArray();
    }


    static long ParseSize(ArgumentResult result)
    {
        if (!SettingParsers.TryParseSize(result.Tokens[0].Value, out var bytes, out var error))
            result.AddError(error!);

        return bytes;
    }
}

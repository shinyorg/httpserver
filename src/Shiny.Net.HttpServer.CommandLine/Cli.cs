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
    const long DefaultMaxUpload = 64 * 1024 * 1024;

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
            Description = "URL prefix the browser is mounted at.",
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
            DefaultValueFactory = _ => DefaultMaxUpload,
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

        var root = new RootCommand("Serves a directory over HTTP with the Shiny.Net.HttpServer file browser.")
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
            verboseOpt
        };

        root.SetAction((parseResult, ct) =>
        {
            var settings = new ServeSettings
            {
                RootPath = Path.GetFullPath(parseResult.GetRequiredValue(pathArg)),
                Address = parseResult.GetRequiredValue(addressOpt),
                Port = parseResult.GetRequiredValue(portOpt),
                UrlPrefix = NormalizePrefix(parseResult.GetRequiredValue(prefixOpt)),
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
                Verbose = parseResult.GetValue(verboseOpt)
            };
            return run(settings, ct);
        });
        return root;
    }


    /// <summary>A prefix is a route, so it needs a leading slash and no trailing one.</summary>
    static string NormalizePrefix(string prefix)
    {
        var value = prefix.Trim();
        if (value.Length == 0 || value == "/")
            return "/";

        if (!value.StartsWith('/'))
            value = "/" + value;

        return value.TrimEnd('/');
    }


    static IPAddress ParseAddress(ArgumentResult result)
    {
        var value = result.Tokens[0].Value;
        switch (value.ToLowerInvariant())
        {
            case "any":
            case "all":
                return IPAddress.Any;

            case "localhost":
            case "loopback":
                return IPAddress.Loopback;
        }

        if (IPAddress.TryParse(value, out var address))
            return address;

        result.AddError($"'{value}' is not an IP address. Use an IP, 'any' or 'localhost'.");
        return IPAddress.Loopback;
    }


    static Permissions ParsePermissions(ArgumentResult result)
    {
        var permissions = Permissions.Read;

        foreach (var token in result.Tokens)
        {
            foreach (var raw in token.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (raw.ToLowerInvariant())
                {
                    case "read":
                        break;

                    case "create":
                        permissions |= Permissions.Create;
                        break;

                    case "update":
                        permissions |= Permissions.Update;
                        break;

                    case "delete":
                        permissions |= Permissions.Delete;
                        break;

                    case "all":
                        permissions |= Permissions.Create | Permissions.Update | Permissions.Delete;
                        break;

                    default:
                        result.AddError($"'{raw}' is not an operation. Use read, create, update, delete or all.");
                        break;
                }
            }
        }
        return permissions;
    }


    static BasicUser[] ParseUsers(ArgumentResult result)
    {
        var users = new List<BasicUser>();

        foreach (var token in result.Tokens)
        {
            var index = token.Value.IndexOf(':');
            if (index < 1 || index == token.Value.Length - 1)
            {
                result.AddError($"'{token.Value}' is not a credential. Use user:password.");
                continue;
            }
            users.Add(new BasicUser(token.Value[..index], token.Value[(index + 1)..]));
        }
        return users.ToArray();
    }


    static long ParseSize(ArgumentResult result)
    {
        var value = result.Tokens[0].Value.Trim().ToLowerInvariant();
        var multiplier = 1L;

        foreach (var (suffix, scale) in new[] { ("gb", 1024L * 1024 * 1024), ("mb", 1024L * 1024), ("kb", 1024L), ("g", 1024L * 1024 * 1024), ("m", 1024L * 1024), ("k", 1024L), ("b", 1L) })
        {
            if (value.EndsWith(suffix))
            {
                multiplier = scale;
                value = value[..^suffix.Length].Trim();
                break;
            }
        }

        if (Int64.TryParse(value, out var number) && number > 0)
            return number * multiplier;

        result.AddError($"'{result.Tokens[0].Value}' is not a size. Use bytes or a suffix like 500k, 64mb, 2gb.");
        return DefaultMaxUpload;
    }
}

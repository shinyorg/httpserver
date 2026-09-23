using System.Net;

namespace Shiny.Net.HttpServer.CommandLine;


public sealed record ServeSettings
{
    /// <summary>Absolute path of the directory being served.</summary>
    public required string RootPath { get; init; }

    public required IPAddress Address { get; init; }
    public required int Port { get; init; }

    /// <summary>Where the directory is mounted - "/" serves it at the root of the site.</summary>
    public required string UrlPrefix { get; init; }

    public required Permissions Permissions { get; init; }
    public required IReadOnlyList<BasicUser> Users { get; init; }
    public required string Realm { get; init; }

    /// <summary>Leaves reads open and puts only writes/deletes behind the login.</summary>
    public required bool AuthChangesOnly { get; init; }

    /// <summary>Sends Basic credentials over unencrypted, non-loopback connections.</summary>
    public required bool AllowInsecureAuth { get; init; }

    public required bool UseHttps { get; init; }

    /// <summary>Opens a pinggy.io quick tunnel, so the directory is reachable from off this network.</summary>
    public required bool UseTunnel { get; init; }

    /// <summary>A pinggy.io access token, which lifts the 60 minute cap an anonymous tunnel has.</summary>
    public string? TunnelToken { get; init; }

    /// <summary>Prints a scannable QR code of the address another device can reach.</summary>
    public bool ShowQr { get; init; } = true;

    public required bool ServeHidden { get; init; }
    public required long MaxUploadBytes { get; init; }
    public required bool Verbose { get; init; }

    /// <summary>Opens the dashboard rather than printing a banner. Ignored when there is no terminal to draw on.</summary>
    public bool UseTui { get; init; }

    public bool AuthEnabled => this.Users.Count > 0;
    public string Scheme => this.UseHttps ? "https" : "http";

    public bool IsLoopbackOnly => IPAddress.IsLoopback(this.Address);


    /// <summary>
    /// Everything that should stop the server before it opens a socket, as the sentence to show -
    /// or null when there is nothing wrong. The command line prints it and exits; the dashboard puts
    /// it under the form and keeps the server it already has.
    /// </summary>
    public string? Validate()
    {
        if (!Directory.Exists(this.RootPath))
            return $"'{this.RootPath}' is not a directory.";

        if (this.Port is < 1 or > 65535)
            return $"{this.Port} is not a port. Use 1-65535.";

        // basic auth is the password itself on every request, so plain HTTP off-box is refused
        if (this.AuthEnabled && !this.UseHttps && !this.IsLoopbackOnly && !this.AllowInsecureAuth)
        {
            return
                $"""
                 Basic auth over plain HTTP on {this.Address} would send the password across the network in the clear on every request.

                 Pick one:
                   --https                  serve over TLS with a self-signed certificate
                   --tunnel -a localhost    reach it only through the tunnel, which is encrypted
                   --address localhost      keep the server on this machine
                   --allow-insecure-auth    send it anyway
                 """;
        }
        return null;
    }


    /// <summary>
    /// Whether moving from <paramref name="previous"/> to this means rebuilding the server, rather
    /// than only opening or closing the tunnel in front of it. Everything but the tunnel, the banner
    /// and the log level is baked into the pipeline when it is built.
    /// </summary>
    public bool NeedsRebuildFrom(ServeSettings previous)
        => this.RootPath != previous.RootPath
           || !this.Address.Equals(previous.Address)
           || this.Port != previous.Port
           || this.UrlPrefix != previous.UrlPrefix
           || this.Permissions != previous.Permissions
           || !this.Users.SequenceEqual(previous.Users)
           || this.Realm != previous.Realm
           || this.AuthChangesOnly != previous.AuthChangesOnly
           || this.AllowInsecureAuth != previous.AllowInsecureAuth
           || this.UseHttps != previous.UseHttps
           || this.ServeHidden != previous.ServeHidden
           || this.MaxUploadBytes != previous.MaxUploadBytes;
}


public sealed record BasicUser(string Username, string Password);

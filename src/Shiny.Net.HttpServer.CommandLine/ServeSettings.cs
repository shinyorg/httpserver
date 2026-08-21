using System.Net;

namespace Shiny.Net.HttpServer.CommandLine;


public sealed record ServeSettings
{
    /// <summary>Absolute path of the directory being served.</summary>
    public required string RootPath { get; init; }

    public required IPAddress Address { get; init; }
    public required int Port { get; init; }

    /// <summary>Where the browser is mounted - "/" serves the directory at the root of the site.</summary>
    public required string UrlPrefix { get; init; }

    public required Permissions Permissions { get; init; }
    public required IReadOnlyList<BasicUser> Users { get; init; }
    public required string Realm { get; init; }

    /// <summary>Leaves reads open and puts only writes/deletes behind the login.</summary>
    public required bool AuthChangesOnly { get; init; }

    /// <summary>Sends Basic credentials over unencrypted, non-loopback connections.</summary>
    public required bool AllowInsecureAuth { get; init; }

    public required bool UseHttps { get; init; }

    /// <summary>Prints a scannable QR code of the address another device can reach.</summary>
    public bool ShowQr { get; init; } = true;

    public required bool ServeHidden { get; init; }
    public required long MaxUploadBytes { get; init; }
    public required bool Verbose { get; init; }

    public bool AuthEnabled => this.Users.Count > 0;
    public string Scheme => this.UseHttps ? "https" : "http";

    public bool IsLoopbackOnly => IPAddress.IsLoopback(this.Address);
}


public sealed record BasicUser(string Username, string Password);

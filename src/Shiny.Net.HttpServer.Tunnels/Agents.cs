using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer.Tunnels;

/// <summary>Settings for the cloudflared agent.</summary>
public sealed class CloudflareTunnelOptions : TunnelAgentOptions
{
    /// <summary>
    /// A named tunnel's token, from the Cloudflare dashboard. Null runs a quick tunnel instead —
    /// no account, a <c>trycloudflare.com</c> hostname, and a URL that changes every run.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>The hostname a named tunnel should route, when the tunnel's config does not say.</summary>
    public string? Hostname { get; set; }
}

/// <summary>
/// Cloudflare Tunnel, through <c>cloudflared</c>.
/// <para>
/// Without a token this is a <b>quick tunnel</b>: nothing to sign up for, a random
/// <c>trycloudflare.com</c> hostname, TLS terminated by Cloudflare, gone when the process ends.
/// Excellent for showing someone a device's UI for ten minutes; not something to build on, because
/// the hostname is new every time and Cloudflare rate-limits them.
/// </para>
/// <code>
/// await server.StartAsync();
/// await using var tunnel = new CloudflareTunnel(new CloudflareTunnelOptions());
/// var url = await tunnel.StartAsync(server);
/// </code>
/// </summary>
public partial class CloudflareTunnel(CloudflareTunnelOptions options, ILogger? logger = null)
    : ProcessTunnelAgent(options, logger)
{
    public override string Name => "cloudflared";

    protected override string ExecutableName => "cloudflared";

    new CloudflareTunnelOptions Options => (CloudflareTunnelOptions)base.Options;

    protected override IEnumerable<string> BuildArguments(int port)
    {
        // Suppressing the updater is not tidiness: cloudflared restarts itself to apply an update,
        // and a tunnel that silently re-dials mid-session is worse than one that stays old.
        yield return "--no-autoupdate";
        yield return "tunnel";

        if (this.Options.Token is { Length: > 0 } token)
        {
            yield return "run";
            yield return "--token";
            yield return token;

            if (this.Options.Hostname is { Length: > 0 } hostname)
            {
                yield return "--hostname";
                yield return hostname;
            }

            yield return "--url";
            yield return $"http://{this.Options.LocalHost}:{port}";
            yield break;
        }

        yield return "--url";
        yield return $"http://{this.Options.LocalHost}:{port}";
    }

    protected override string? ParseUrl(string line)
    {
        // A named tunnel never prints a URL — it routes a hostname that was configured elsewhere —
        // so that is what is reported back.
        if (this.Options.Token is { Length: > 0 })
        {
            return this.Options.Hostname is { Length: > 0 } hostname && line.Contains("Registered tunnel connection", StringComparison.Ordinal)
                ? "https://" + hostname
                : null;
        }

        var match = QuickTunnelUrl().Match(line);

        return match.Success ? match.Value : null;
    }

    [GeneratedRegex(@"https://[a-z0-9\-]+\.trycloudflare\.com", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuickTunnelUrl();
}

/// <summary>Settings for the ngrok agent.</summary>
public sealed class NgrokTunnelOptions : TunnelAgentOptions
{
    /// <summary>
    /// The account authtoken. Null relies on whatever <c>ngrok config</c> already stored, which is
    /// the usual case on a developer machine.
    /// </summary>
    public string? AuthToken { get; set; }

    /// <summary>A reserved domain to bind, for an account that has one.</summary>
    public string? Domain { get; set; }

    /// <summary>The region to connect to — <c>us</c>, <c>eu</c>, <c>ap</c>, <c>au</c>, <c>sa</c>, <c>jp</c>, <c>in</c>.</summary>
    public string? Region { get; set; }
}

/// <summary>
/// ngrok, through the <c>ngrok</c> agent.
/// <para>
/// Started with structured logging on stdout rather than the interactive terminal UI, because the
/// UI redraws in place and there is nothing in it to parse. The URL is read from the
/// <c>started tunnel</c> log line.
/// </para>
/// </summary>
public partial class NgrokTunnel(NgrokTunnelOptions options, ILogger? logger = null)
    : ProcessTunnelAgent(options, logger)
{
    public override string Name => "ngrok";

    protected override string ExecutableName => "ngrok";

    new NgrokTunnelOptions Options => (NgrokTunnelOptions)base.Options;

    protected override IEnumerable<string> BuildArguments(int port)
    {
        yield return "http";
        yield return $"{this.Options.LocalHost}:{port}";

        // Structured logs to stdout. Without this the agent draws a terminal dashboard and the URL
        // never appears in a line anything can read.
        yield return "--log";
        yield return "stdout";
        yield return "--log-format";
        yield return "logfmt";

        if (this.Options.AuthToken is { Length: > 0 } authToken)
        {
            yield return "--authtoken";
            yield return authToken;
        }

        if (this.Options.Domain is { Length: > 0 } domain)
        {
            yield return "--domain";
            yield return domain;
        }

        if (this.Options.Region is { Length: > 0 } region)
        {
            yield return "--region";
            yield return region;
        }
    }

    protected override string? ParseUrl(string line)
    {
        if (!line.Contains("started tunnel", StringComparison.OrdinalIgnoreCase))
            return null;

        var match = TunnelUrl().Match(line);

        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"url=(https://[^\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TunnelUrl();
}

/// <summary>Settings for Tailscale Funnel.</summary>
public sealed class TailscaleFunnelOptions : TunnelAgentOptions
{
    /// <summary>
    /// Serves on the tailnet only, rather than publishing to the internet.
    /// <para>
    /// This is <c>tailscale serve</c> instead of <c>tailscale funnel</c>, and it is the safer
    /// default for a device: the URL works for machines already on your tailnet and for nobody
    /// else, which is usually the whole requirement.
    /// </para>
    /// </summary>
    public bool TailnetOnly { get; set; }
}

/// <summary>
/// Tailscale Funnel, through the <c>tailscale</c> CLI.
/// <para>
/// Unlike the other two this needs the machine to already be on a tailnet, and Funnel has to be
/// enabled for the node in the admin console. In exchange the hostname is stable, it is yours, and
/// the certificate is issued automatically.
/// </para>
/// </summary>
public partial class TailscaleFunnel(TailscaleFunnelOptions options, ILogger? logger = null)
    : ProcessTunnelAgent(options, logger)
{
    public override string Name => "tailscale";

    protected override string ExecutableName => "tailscale";

    new TailscaleFunnelOptions Options => (TailscaleFunnelOptions)base.Options;

    protected override IEnumerable<string> BuildArguments(int port)
    {
        yield return this.Options.TailnetOnly ? "serve" : "funnel";
        yield return "--bg=false";
        yield return $"http://{this.Options.LocalHost}:{port}";
    }

    protected override string? ParseUrl(string line)
    {
        var match = TailnetUrl().Match(line);

        return match.Success ? match.Value : null;
    }

    [GeneratedRegex(@"https://[a-z0-9\-]+\.[a-z0-9\-]+\.ts\.net(?::\d+)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TailnetUrl();
}

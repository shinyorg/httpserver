using Shiny.Net.HttpServer.Ssh;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace Shiny.Net.HttpServer.CommandLine.Tui;


/// <summary>
/// The console banner, kept current: where the directory can be reached, how it is set up, what is
/// risky about that, and a code a phone can scan.
/// </summary>
sealed class OverviewTab(Dashboard dashboard)
{
    Visual? view;

    public Visual View => this.view ??= new ScrollViewer(
        new ComputedVisual(() =>
        {
            _ = dashboard.SessionRevision.Value;
            return this.Build();
        })
    );


    Visual Build()
    {
        var session = dashboard.Session;
        if (session.Settings is not { } settings)
            return new Padder(Ui.Muted("Starting…")).Padding(new Thickness(1));

        var tunnelUrl = session.TunnelUrl is { } raw ? ServerUrls.Tunnel(raw, settings.UrlPrefix) : null;
        var facts = new List<(string, string)>
        {
            ("Directory", Ui.Escape(settings.RootPath))
        };

        if (session.State == SessionState.Running)
        {
            foreach (var url in ServerUrls.All(settings))
                facts.Add(("URL", $"[cyan]{Ui.Escape(url)}[/]"));
        }
        else
        {
            facts.Add(("URL", session.Error is { } error ? $"[red]{Ui.Escape(error)}[/]" : "[dim]not listening[/]"));
        }

        if (settings.UseTunnel)
        {
            facts.Add(("Tunnel", session.TunnelState switch
            {
                QuickTunnelState.Connected when tunnelUrl is not null => $"[cyan]{Ui.Escape(tunnelUrl)}[/]",
                QuickTunnelState.Connecting => "[yellow]connecting…[/]",
                QuickTunnelState.Reconnecting => "[yellow]reconnecting - the old address is gone[/]",
                QuickTunnelState.Failed => $"[red]{Ui.Escape(session.TunnelError ?? "failed")}[/]",
                _ => session.TunnelError is { } tunnelError ? $"[red]{Ui.Escape(tunnelError)}[/]" : "[dim]closed[/]"
            }));
        }

        facts.Add(("Operations", Ui.Escape(settings.Permissions.Describe())));
        facts.Add(("Mount", "WebDAV - Finder, Explorer and any WebDAV client can open the URL as a drive"));
        facts.Add((
            "Auth",
            settings.AuthEnabled
                ? Ui.Escape($"basic ({String.Join(", ", settings.Users.Select(x => x.Username))}){(settings.AuthChangesOnly ? ", changes only" : "")}")
                : "none"
        ));
        facts.Add(("TLS", settings.UseHttps ? "self-signed certificate" : "[dim]off[/]"));

        if (settings.Permissions.AllowsChanges())
            facts.Add(("Max upload", Ui.Bytes(settings.MaxUploadBytes)));

        var left = new VStack(Ui.Facts(facts)).Spacing(1);
        foreach (var warning in Warnings(settings, tunnelUrl is not null))
            left.Add(Ui.Warning(warning));

        // The tunnel address is the one worth scanning when there is one: it reaches a phone that is
        // not on this network at all, which the LAN address does not.
        var shareable = tunnelUrl ?? (session.State == SessionState.Running ? ServerUrls.Shareable(settings) : null);
        var qr = settings.ShowQr && shareable is not null ? Qr(shareable) : Ui.Nothing();

        return new Padder(new WrapHStack().Children([left.MaxWidth(90), qr]).Spacing(4)).Padding(new Thickness(1));
    }


    /// <summary>The same warnings the banner prints, in the same words, and for the same reasons.</summary>
    static IEnumerable<string> Warnings(ServeSettings settings, bool tunnelOpen)
    {
        if (tunnelOpen)
        {
            yield return "The tunnel is public: anyone holding the address can reach this directory, and the traffic passes through pinggy.io."
                         + (settings.TunnelToken is { Length: > 0 } ? "" : " An anonymous tunnel stops after 60 minutes.");
        }

        if (settings.Permissions.AllowsChanges() && !settings.AuthEnabled)
        {
            yield return tunnelOpen
                ? "Writes are open to anyone on the internet holding the tunnel address. Add a user in Settings."
                : "Writes are open to anyone who can reach this server. Add a user in Settings.";
        }

        if (settings.UseHttps)
            yield return "The certificate is self-signed, so clients will not trust it.";
    }


    /// <summary>
    /// Black on white whatever the theme: a reader wants dark modules on a light field, and a dark
    /// terminal would otherwise hand it the negative of the code.
    /// </summary>
    static Visual Qr(string url)
    {
        if (!QrCode.TryEncode(url, out var code))
            return Ui.Nothing();

        var stack = new VStack();
        foreach (var line in QrConsole.Render(code))
            stack.Add(Ui.Text($"[black on white]{line}[/]"));

        stack.Add(Ui.Text($"[dim]{Ui.Escape(url)}[/]"));
        return stack;
    }
}

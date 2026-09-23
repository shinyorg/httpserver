using System.Globalization;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;

namespace Shiny.Net.HttpServer.CommandLine.Tui;


/// <summary>
/// Every option the command line takes, as a form, applied to the running server.
/// </summary>
/// <remarks>
/// <para>
/// Read through the same parsers as the command line, so a value the form accepts is a value the
/// flag would have accepted, with the same error when it is not.
/// </para>
/// <para>
/// Most changes rebuild the server, which cuts off anything in flight. That is said before it
/// happens, and only when there is something in flight to cut off - opening or closing the tunnel,
/// or switching the log level, touches nothing and asks nothing.
/// </para>
/// </remarks>
sealed class SettingsTab(Dashboard dashboard)
{
    readonly State<string?> path = new("");
    readonly State<string?> address = new("");
    readonly State<string?> port = new("");
    readonly State<string?> prefix = new("");
    readonly State<bool> create = new(false);
    readonly State<bool> update = new(false);
    readonly State<bool> delete = new(false);
    readonly State<string?> users = new("");
    readonly State<string?> realm = new("");
    readonly State<bool> authChangesOnly = new(false);
    readonly State<bool> allowInsecureAuth = new(false);
    readonly State<bool> https = new(false);
    readonly State<bool> tunnel = new(false);
    readonly State<string?> tunnelToken = new("");
    readonly State<bool> hidden = new(false);
    readonly State<string?> maxUpload = new("");
    readonly State<bool> showQr = new(true);
    readonly State<bool> verbose = new(false);
    readonly State<string> message = new("");

    ServeSettings? loaded;
    Visual? view;
    TextBox? first;

    /// <summary>Where the cursor goes when the tab is opened from the keyboard.</summary>
    public Visual? FocusTarget => this.first;

    public Visual View => this.view ??= this.Build();


    /// <summary>Fills the form from what is running. Render thread.</summary>
    public void Load(ServeSettings settings)
    {
        this.loaded = settings;

        this.path.Value = settings.RootPath;
        this.address.Value = SettingParsers.FormatAddress(settings.Address);
        this.port.Value = settings.Port.ToString(CultureInfo.InvariantCulture);
        this.prefix.Value = settings.UrlPrefix;
        this.create.Value = settings.Permissions.Has(Permissions.Create);
        this.update.Value = settings.Permissions.Has(Permissions.Update);
        this.delete.Value = settings.Permissions.Has(Permissions.Delete);
        this.users.Value = String.Join(Environment.NewLine, settings.Users.Select(x => $"{x.Username}:{x.Password}"));
        this.realm.Value = settings.Realm;
        this.authChangesOnly.Value = settings.AuthChangesOnly;
        this.allowInsecureAuth.Value = settings.AllowInsecureAuth;
        this.https.Value = settings.UseHttps;
        this.tunnel.Value = settings.UseTunnel;
        this.tunnelToken.Value = settings.TunnelToken ?? "";
        this.hidden.Value = settings.ServeHidden;
        this.maxUpload.Value = SettingParsers.FormatSize(settings.MaxUploadBytes);
        this.showQr.Value = settings.ShowQr;
        this.verbose.Value = settings.Verbose;
    }


    /// <summary>What happened to the last apply, under the form where the button was.</summary>
    public void ShowResult(string? error)
        => this.message.Value = error is null ? "[green]Applied.[/]" : $"[red]{Ui.Escape(error)}[/]";


    Visual Build()
    {
        var form = new VStack(
            Ui.Section("Directory", new VStack(
                Field("Path", this.first = Input(this.path), "The directory to serve."),
                Field("Mounted at", Input(this.prefix), "URL prefix - / serves it at the root of the site."),
                Toggle("Include hidden files and dotfiles", this.hidden)
            ).Spacing(1)),

            Ui.Section("Listen", new VStack(
                new HStack(
                    Field("Address", Input(this.address).MinWidth(24), "An IP, any, or localhost."),
                    Field("Port", Input(this.port).MinWidth(8), null)
                ).Spacing(2),
                Toggle("HTTPS with a self-signed certificate", this.https),
                Toggle("Show a QR code on the overview", this.showQr)
            ).Spacing(1)),

            Ui.Section("Tunnel", new VStack(
                Toggle("Open a public pinggy.io tunnel", this.tunnel),
                Field(
                    "Access token",
                    Input(this.tunnelToken).IsPassword(true),
                    "Optional. Lifts the 60 minute cap an anonymous tunnel has. Changing it opens a new tunnel on a new address."
                )
            ).Spacing(1)),

            Ui.Section("Operations", new VStack(
                Ui.Muted("Read is always allowed."),
                new HStack(
                    Toggle("Create", this.create),
                    Toggle("Update", this.update),
                    Toggle("Delete", this.delete)
                ).Spacing(3),
                Field("Largest upload", Input(this.maxUpload).MaxWidth(16), "Bytes, or with a suffix: 500k, 64mb, 2gb.")
            ).Spacing(1)),

            Ui.Section("Authentication", new VStack(
                Field(
                    "Users",
                    new TextArea(this.users).MinHeight(3),
                    "One user:password per line. Empty turns basic auth off."
                ),
                Field("Realm", Input(this.realm).MaxWidth(40), null),
                Toggle("Only ask for a login to create, update or delete", this.authChangesOnly),
                Toggle("Allow basic auth over plain HTTP off this machine (the password crosses the network in the clear)", this.allowInsecureAuth)
            ).Spacing(1)),

            Ui.Section("Diagnostics", Toggle("Verbose log (debug level, and the tunnel's own messages)", this.verbose)),

            Ui.Live(() => this.message.Value).Wrap(true),

            Ui.Toolbar(
                Ui.Primary("Apply", this.Submit),
                Ui.Action("Revert", this.Revert),
                Ui.Live(() => dashboard.Applying.Value ? "[yellow]applying…[/]" : "[dim]Ctrl+S applies from anywhere[/]")
            )
        ).Spacing(1);

        return new ScrollViewer(new Padder(form.MaxWidth(100)).Padding(new Thickness(1)));
    }


    /// <summary>
    /// A field's text, trimmed and without control characters. A shortcut pressed in the same burst
    /// as typing can land in the box as a character, and "18282\x13" is a port nobody meant to reject.
    /// </summary>
    static string Clean(string? value)
        => new string((value ?? "").Where(x => !Char.IsControl(x) || x is '\n' or '\r').ToArray()).Trim();


    /// <summary>
    /// A check box that also answers Space. Enter is the control's own key; Space is the one people
    /// reach for, and it arrives as a key or as a character depending on the terminal.
    /// </summary>
    static CheckBox Toggle(string label, State<bool> value)
    {
        var box = new CheckBox(label).IsChecked(value);
        box.AddKeyBinding(new KeyGesture(TerminalKey.Space), () => value.Value = !value.Value);
        box.AddKeyBinding(new KeyGesture(' '), () => value.Value = !value.Value);
        return box;
    }


    static TextBox Input(State<string?> value) => new TextBox(value).HorizontalAlignment(Align.Stretch);


    static Visual Field(string label, Visual input, string? hint)
    {
        var stack = new VStack(Ui.Text($"[dim]{Ui.Escape(label)}[/]"), input);
        if (hint is not null)
            stack.Add(Ui.Muted(hint));

        return stack;
    }


    void Revert()
    {
        if (dashboard.Session.Settings is { } running)
            this.Load(running);

        this.message.Value = "[dim]Back to what is running.[/]";
    }


    /// <summary>Reads the form, and applies it if it reads - asking first when that would cut transfers off.</summary>
    public void Submit()
    {
        if (!this.TryRead(out var next, out var error))
        {
            this.message.Value = $"[red]{Ui.Escape(error)}[/]";
            return;
        }

        if (next.Validate() is { } invalid)
        {
            this.message.Value = $"[red]{Ui.Escape(invalid)}[/]";
            return;
        }

        var running = dashboard.Session.Settings;
        var rebuild = running is null || next.NeedsRebuildFrom(running);
        var inFlight = dashboard.Monitor.ActiveCount;

        if (rebuild && inFlight > 0)
        {
            dashboard.Confirm(
                "Restart the server?",
                $"These settings rebuild the server, which cuts off {inFlight} request(s) still running - uploads included. Apply anyway?",
                "Restart",
                () => this.Go(next)
            );
            return;
        }

        this.Go(next);
    }


    void Go(ServeSettings next)
    {
        this.message.Value = "[yellow]Applying…[/]";
        dashboard.Apply(next);
    }


    bool TryRead(out ServeSettings settings, out string error)
    {
        settings = null!;
        error = "";

        var root = Clean(this.path.Value);
        if (root.Length == 0)
        {
            error = "Enter a directory to serve.";
            return false;
        }

        if (!SettingParsers.TryParseAddress(Clean(this.address.Value), out var ip, out var addressError))
        {
            error = addressError!;
            return false;
        }

        if (!Int32.TryParse(Clean(this.port.Value), NumberStyles.None, CultureInfo.InvariantCulture, out var portNumber))
        {
            error = $"'{Clean(this.port.Value)}' is not a port. Use 1-65535.";
            return false;
        }

        if (!SettingParsers.TryParseSize(Clean(this.maxUpload.Value), out var maxBytes, out var sizeError))
        {
            error = sizeError!;
            return false;
        }

        var credentials = new List<BasicUser>();
        foreach (var line in Clean(this.users.Value).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Not the parser's message: it quotes the line back, and half of that line is a password.
            if (!SettingParsers.TryParseUser(line, out var user, out _))
            {
                error = "A line in Users is not a credential. Use user:password, one per line.";
                return false;
            }
            credentials.Add(user!);
        }

        var permissions = Permissions.Read;
        if (this.create.Value)
            permissions |= Permissions.Create;
        if (this.update.Value)
            permissions |= Permissions.Update;
        if (this.delete.Value)
            permissions |= Permissions.Delete;

        var token = Clean(this.tunnelToken.Value);

        settings = new ServeSettings
        {
            RootPath = Path.GetFullPath(root),
            Address = ip,
            Port = portNumber,
            UrlPrefix = SettingParsers.NormalizePrefix(Clean(this.prefix.Value)),
            Permissions = permissions,
            Users = credentials,
            Realm = Clean(this.realm.Value) is { Length: > 0 } realmName ? realmName : "shinyhttpserver",
            AuthChangesOnly = this.authChangesOnly.Value,
            AllowInsecureAuth = this.allowInsecureAuth.Value,
            UseHttps = this.https.Value,
            UseTunnel = this.tunnel.Value,
            TunnelToken = token.Length > 0 ? token : null,
            ServeHidden = this.hidden.Value,
            MaxUploadBytes = maxBytes,
            ShowQr = this.showQr.Value,
            Verbose = this.verbose.Value,
            UseTui = this.loaded?.UseTui ?? true
        };
        return true;
    }
}

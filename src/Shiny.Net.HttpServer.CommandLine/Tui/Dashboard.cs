using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer.CommandLine.Monitoring;
using Shiny.Net.HttpServer.Ssh;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;

namespace Shiny.Net.HttpServer.CommandLine.Tui;


/// <summary>
/// The full-screen view of a running server: where it can be reached, what it is being asked for,
/// what is moving right now, and a form to change how it is set up without restarting the tool.
/// </summary>
/// <remarks>
/// <para>
/// Every <see cref="State{T}"/> is written on the render thread. The server, the tunnel and the
/// monitor all change on threads of their own, so the dashboard does not listen to them: it reads
/// them once per tick and lets the diffing renderer decide whether anything on screen moved. That is
/// a quarter-second of latency on a number going up, which is invisible, in exchange for having no
/// cross-thread writes to get wrong.
/// </para>
/// </remarks>
sealed class Dashboard
{
    static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    readonly CancellationTokenSource lifetime = new();
    readonly State<bool> applying = new(false);
    readonly State<int> sessionRevision = new(0);
    readonly State<string> statusLeft = new("");

    ToastHost toasts = null!;
    WindowLayer windows = null!;
    TabControl tabs = null!;
    TerminalApp? app;
    string sessionFingerprint = "";

    Dashboard(ServerSession session, TrafficMonitor monitor, TuiLog log)
    {
        this.Session = session;
        this.Monitor = monitor;
        this.Log = log;

        this.Overview = new OverviewTab(this);
        this.Requests = new RequestsTab(this);
        this.Transfers = new TransfersTab(this);
        this.Settings = new SettingsTab(this);
        this.LogTab = new LogTab(this);

        // Raised on the tunnel's thread; the toast is posted, not shown from there.
        session.TunnelAddressChanged += url => this.Post(() =>
        {
            this.Warn("The tunnel reconnected on a new address. The previous one no longer answers.");
            this.Log.Write(LogLevel.Warning, "tunnel", $"new address {url}");
        });
    }

    public ServerSession Session { get; }
    public TrafficMonitor Monitor { get; }
    public TuiLog Log { get; }

    public OverviewTab Overview { get; }
    public RequestsTab Requests { get; }
    public TransfersTab Transfers { get; }
    public SettingsTab Settings { get; }
    public LogTab LogTab { get; }

    /// <summary>Bumped whenever the session's state, addresses or settings move. The overview hangs off it.</summary>
    public State<int> SessionRevision => this.sessionRevision;

    /// <summary>True while a change of settings is being applied.</summary>
    public State<bool> Applying => this.applying;


    public static async Task<int> RunAsync(ServeSettings settings, CancellationToken cancellationToken)
    {
        var log = new TuiLog { Verbose = settings.Verbose };
        var monitor = new TrafficMonitor();

        await using var session = new ServerSession(
            monitor,
            x => x
                .ClearProviders()
                .AddProvider(log)

                // The provider decides, so the verbose switch works without a rebuild.
                .SetMinimumLevel(LogLevel.Trace)
        );

        var dashboard = new Dashboard(session, monitor, log);
        var root = dashboard.Build();
        var started = false;

        await Terminal.RunAsync(
            root,
            context =>
            {
                if (!started)
                {
                    started = true;

                    // The first tick is the earliest there is a dispatcher to post back through, so
                    // the server is started here rather than before the loop.
                    dashboard.app = context.App;
                    dashboard.Settings.Load(settings);
                    dashboard.Apply(settings);
                }

                dashboard.Tick();
                return TerminalLoopResult.Continue;
            },
            new TerminalRunOptions
            {
                // Polling rather than Auto: transfers move without anyone touching the keyboard, and
                // an event-driven loop would leave their progress bars standing still.
                LoopMode = TerminalLoopMode.Polling,
                UpdateWaitDuration = TickInterval
            },
            cancellationToken
        ).ConfigureAwait(false);

        await dashboard.lifetime.CancelAsync().ConfigureAwait(false);

        var ran = session.State == SessionState.Running;
        Console.WriteLine();
        Console.WriteLine("stopped");
        return ran ? 0 : 1;
    }


    Visual Build()
    {
        this.tabs = new TabControl(
            // Stretched, or a tab is only as wide as what is in it and the grid's widest column
            // has nothing left over to fill.
            new TabPage(Ui.Text("Overview"), this.Overview.View.Stretch()),
            new TabPage(Ui.Text("Requests"), this.Requests.View.Stretch()),
            new TabPage(Ui.Text("Transfers"), this.Transfers.View.Stretch()),
            new TabPage(Ui.Text("Settings"), this.Settings.View.Stretch()),
            new TabPage(Ui.Text("Log"), this.LogTab.View.Stretch())
        );

        var header = new HStack(
            Ui.Text("[bold]shinyhttpserver[/]"),
            Ui.Live(this.HeaderMarkup)
        ).Spacing(2);

        var status = new StatusBar(
            Ui.Live(() => this.statusLeft.Value),
            Ui.Text("[dim]F1-F5 tabs · Ctrl+L clear · Ctrl+S apply · Ctrl+Q quit[/]")
        );

        this.toasts = new ToastHost(new DockLayout(new Padder(header).Padding(new Thickness(1, 0, 1, 0)), this.tabs, status))
            .Position(ToastPosition.BottomRight);

        this.windows = new WindowLayer(this.toasts);
        this.RegisterCommands(this.windows);
        return this.windows;
    }


    void RegisterCommands(Visual root)
    {
        TerminalKey[] keys = [TerminalKey.F1, TerminalKey.F2, TerminalKey.F3, TerminalKey.F4, TerminalKey.F5];
        string[] names = ["Overview", "Requests", "Transfers", "Settings", "Log"];

        for (var i = 0; i < keys.Length; i++)
        {
            var index = i;
            root.AddCommand(new Command
            {
                Id = $"tab.{names[i].ToLowerInvariant()}",
                Name = names[i],
                LabelMarkup = names[i],
                Gesture = new KeyGesture(keys[i]),
                Execute = _ => this.ShowTab(index)
            });
        }

        root.AddCommand(new Command
        {
            Id = "requests.clear",
            Name = "Clear requests",
            LabelMarkup = "Clear requests",
            Gesture = new KeyGesture(TerminalChar.CtrlL, TerminalModifiers.Ctrl),
            Execute = _ => this.Requests.Clear()
        });

        root.AddCommand(new Command
        {
            Id = "settings.apply",
            Name = "Apply settings",
            LabelMarkup = "Apply settings",
            Gesture = new KeyGesture(TerminalChar.CtrlS, TerminalModifiers.Ctrl),
            Execute = _ => this.Settings.Submit()
        });
    }


    /// <summary>
    /// Opens a tab and puts the cursor in it. A tab's own keys - Enter on a request, typing in a field -
    /// are routed from the focused control, so a tab that only looked open would not answer them.
    /// </summary>
    void ShowTab(int index)
    {
        this.tabs.SelectedIndex = index;

        Visual? target = index switch
        {
            1 => this.Requests.FocusTarget,
            3 => this.Settings.FocusTarget,
            4 => this.LogTab.FocusTarget,
            _ => null
        };

        // Posted: the tab's content joins the tree on the next layout, and a control that is not in
        // the tree yet cannot take focus.
        if (target is not null && this.app is { } live)
            live.Post(() => live.Focus(target));
    }


    /// <summary>One pass of reading the world into the screen. Render thread.</summary>
    void Tick()
    {
        var fingerprint = String.Join(
            '|',
            this.Session.State,
            this.Session.Error,
            this.Session.TunnelState,
            this.Session.TunnelUrl,
            this.Session.TunnelError,
            this.Session.Settings?.GetHashCode()
        );

        if (fingerprint != this.sessionFingerprint)
        {
            this.sessionFingerprint = fingerprint;
            this.sessionRevision.Value = this.sessionRevision.Value + 1;
        }

        this.Requests.Tick();
        this.Transfers.Tick();
        this.LogTab.Tick();

        var monitor = this.Monitor;
        this.statusLeft.Value =
            $"{monitor.TotalRequests:N0} requests · {monitor.ActiveCount} active · "
            + $"[dim]in[/] {Ui.Bytes(monitor.TotalBytesIn)} · [dim]out[/] {Ui.Bytes(monitor.TotalBytesOut)}";
    }


    string HeaderMarkup()
    {
        _ = this.sessionRevision.Value;

        var session = this.Session;
        var state = this.applying.Value
            ? "[yellow]● applying…[/]"
            : session.State switch
            {
                SessionState.Running => "[green]● running[/]",
                SessionState.Starting => "[yellow]● starting[/]",
                SessionState.Failed => "[red]● not listening[/]",
                _ => "[dim]● stopped[/]"
            };

        var address = session.Settings is { } settings && session.State == SessionState.Running
            ? session.TunnelUrl is { } tunnel
                ? ServerUrls.Tunnel(tunnel, settings.UrlPrefix)
                : ServerUrls.Shareable(settings) ?? ServerUrls.All(settings).First()
            : null;

        var tunnelMark = session.TunnelState switch
        {
            QuickTunnelState.Connecting => "  [yellow]tunnel connecting…[/]",
            QuickTunnelState.Reconnecting => "  [yellow]tunnel reconnecting…[/]",
            QuickTunnelState.Failed => "  [red]tunnel failed[/]",
            _ => ""
        };

        return address is null ? state + tunnelMark : $"{state}  {Ui.Escape(address)}{tunnelMark}";
    }


    /// <summary>
    /// Moves the server over to <paramref name="next"/> in the background, and reports how it went.
    /// </summary>
    public void Apply(ServeSettings next)
    {
        if (this.applying.Value)
        {
            this.Warn("Still applying the last change.");
            return;
        }

        // Starting up goes through here too, and "Settings applied." on launch is noise - so the
        // first apply only speaks up when it fails.
        var initial = this.Session.Settings is null;

        this.applying.Value = true;
        this.Log.Write(LogLevel.Information, "dashboard", initial ? "starting" : "applying settings");

        _ = Task.Run(async () =>
        {
            string? error;
            try
            {
                error = await this.Session.ApplyAsync(next, this.lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            this.Post(() =>
            {
                this.applying.Value = false;

                // Whatever is running now is what the form should say - the new settings, or the old
                // ones after a rollback.
                if (this.Session.Settings is { } running)
                {
                    this.Log.Verbose = running.Verbose;
                    this.Settings.Load(running);
                }

                if (!initial || error is not null)
                    this.Settings.ShowResult(error);

                if (error is null && initial)
                {
                    this.Log.Write(LogLevel.Information, "dashboard", "listening");
                }
                else if (error is null)
                {
                    this.Success("Settings applied.");
                    this.Log.Write(
                        LogLevel.Information,
                        "dashboard",
                        this.Session.LastApplyRebuilt ? "settings applied - server rebuilt" : "settings applied - server left running"
                    );
                }
                else
                {
                    this.Error(error);
                    this.Log.Write(LogLevel.Error, "dashboard", error);
                }

                if (this.Session.TunnelError is { } tunnelError)
                {
                    this.Warn(tunnelError);
                    this.Log.Write(LogLevel.Warning, "tunnel", tunnelError);
                }
            });
        });
    }


    // ---- threading, notifications and dialogs ----

    /// <summary>Marshals onto the render thread. Every write to a state from background work goes through this.</summary>
    public void Post(Action action)
    {
        if (this.app is null)
            action();
        else
            this.app.Post(action);
    }

    public void Info(string message) => this.toasts.Show(Ui.Text(Ui.Escape(message)), ToastSeverity.Info);
    public void Success(string message) => this.toasts.Show(Ui.Text(Ui.Escape(message)), ToastSeverity.Success);
    public void Warn(string message) => this.toasts.Show(Ui.Text(Ui.Escape(message)), ToastSeverity.Warning);
    public void Error(string message) => this.toasts.Show(Ui.Text(Ui.Escape(message)), ToastSeverity.Error);


    /// <summary>A modal with a title, content and a row of buttons built against the dialog so they can close it.</summary>
    public Dialog OpenDialog(string title, Visual content, Func<Dialog, Visual[]> buttons)
    {
        var row = new HStack().Spacing(1).HorizontalAlignment(Align.End);

        var dialog = new Dialog(
                Ui.Text($"[bold]{Ui.Escape(title)}[/]"),
                new VStack(content, new Rule(), row).Spacing(1)
            )
            .Padding(new Thickness(1))
            .IsModal(true)
            .IsDraggable(true);

        dialog.AddKeyBinding(new KeyGesture(TerminalKey.Escape), () => this.CloseDialog(dialog));
        row.Add(buttons(dialog));

        this.windows.AddWindow(dialog);
        return dialog;
    }

    public void CloseDialog(Visual dialog) => this.windows.RemoveWindow(dialog);


    /// <summary>A yes/no with the destructive choice coloured and the safe one focused.</summary>
    public void Confirm(string title, string message, string confirmLabel, Action onConfirm)
        => this.OpenDialog(
            title,
            Ui.Text(Ui.Escape(message)).Wrap(true).MaxWidth(70),
            dialog =>
            [
                Ui.Action("Cancel", () => this.CloseDialog(dialog)).AutoFocus(true),
                Ui.Danger(confirmLabel, () =>
                {
                    this.CloseDialog(dialog);
                    onConfirm();
                })
            ]
        );
}

using System.Net;
using Foundation;
using Shiny.Net.HttpServer;
using UIKit;

namespace Sample.TvOS;

/// <summary>
/// The whole sample: a server on the LAN that follows the app to the foreground.
/// <para>
/// tvOS suspends a backgrounded app exactly as iOS does, and there is no background mode that
/// legitimately holds a listener open on either. So the useful behaviour is not "keep serving" -
/// that is not on offer - it is to be serving again by the time anyone is looking at the app, and
/// to make the fact that it stopped visible rather than leaving a listener that reports itself
/// running with nothing behind it.
/// </para>
/// <para>
/// The lifecycle is wired to UIKit by hand here. <c>Shiny.Net.HttpServer.Mobile</c> does exactly
/// this and rather more - a bounded retry for the bind a half-woken network refuses, a rebind when
/// the device changes network - but it does not target tvOS yet, because Shiny.Core does not. When
/// it does, the two overrides below become <c>AddHttpServerLifecycle()</c> and this file gets
/// shorter. The core server needed no changes for any of this: it is plain <c>net10.0</c>, and a
/// tvOS app references it the way anything else does.
/// </para>
/// </summary>
[Register(nameof(AppDelegate))]
public class AppDelegate : UIApplicationDelegate
{
    HttpServer? server;
    ServerViewController? controller;

    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        this.controller = new ServerViewController();

        // The window-and-delegate lifecycle rather than UIWindowScene. It is obsoleted from
        // tvOS 26 and still works, and the scene-based equivalent is a manifest entry, a scene
        // delegate and a configuration dictionary - all of it ceremony that would bury the six
        // lines this sample is actually about. A real app should adopt scenes.
#pragma warning disable CA1422
        this.Window = new UIWindow(UIScreen.MainScreen.Bounds)
        {
            RootViewController = this.controller
        };
#pragma warning restore CA1422
        this.Window.MakeKeyAndVisible();

        this.server = BuildServer();

        return true;
    }

    // Not FinishedLaunching: a cold start goes through here too, so starting in one place covers
    // both the first foreground and every one after it.
    public override void WillEnterForeground(UIApplication application) => _ = this.StartAsync();

    public override void OnActivated(UIApplication application)
    {
        // WillEnterForeground is not raised on the very first activation of a cold start.
        if (this.server is { IsRunning: false })
            _ = this.StartAsync();
    }

    public override void DidEnterBackground(UIApplication application) => _ = this.StopAsync();

    static HttpServer BuildServer()
    {
        var builder = HttpServer.CreateBuilder();

        builder.Configure(o =>
        {
            // Any, not Loopback: the point is to be reachable from the other devices on the
            // network. A TV has no browser to test a loopback listener with anyway.
            o.Address = IPAddress.Any;
            o.Port = 8080;
        });

        var server = builder.Build();

        server.MapGet("/", _ => Results.Text(StatusPage(), "text/html; charset=utf-8"));
        server.MapGet("/ping", _ => Results.Text("pong"));

        return server;
    }

    async Task StartAsync()
    {
        if (this.server is not { IsRunning: false } target)
            return;

        try
        {
            await target.StartAsync().ConfigureAwait(false);
            this.Report();
        }
        catch (Exception ex)
        {
            // A bind refused because the network has not finished waking is the common one, and it
            // is why the Mobile package retries. Shown rather than swallowed: a sample that fails
            // silently teaches the failure mode this repo keeps trying to eliminate.
            this.Report(ex);
        }
    }

    async Task StopAsync()
    {
        if (this.server is not { IsRunning: true } target)
            return;

        try
        {
            await target.StopAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Going to the background regardless.
        }

        this.Report();
    }

    void Report(Exception? error = null)
    {
        var running = this.server?.IsRunning ?? false;
        var addresses = LocalAddresses.Current().Select(a => $"http://{a}:8080").ToArray();

        // UIKit is main-thread only and both transitions above complete off it.
        UIApplication.SharedApplication.InvokeOnMainThread(
            () => this.controller?.Update(running, addresses, error)
        );
    }

    static string StatusPage()
        => """
           <!doctype html>
           <meta charset="utf-8">
           <meta name="viewport" content="width=device-width,initial-scale=1">
           <title>Shiny.Net.HttpServer on tvOS</title>
           <style>
             body { font: 16px/1.6 system-ui, sans-serif; max-width: 34rem; margin: 12vh auto; padding: 0 1.5rem; }
             code { background: #f4f4f5; padding: .15em .4em; border-radius: .25rem; }
           </style>
           <h1>Served from an Apple TV</h1>
           <p>
             This page came from <code>Shiny.Net.HttpServer</code> running inside a tvOS app, on a
             platform where ASP.NET Core cannot go.
           </p>
           <p>The server stops when the app is backgrounded and starts again on resume.</p>
           <p><a href="/ping">/ping</a></p>
           """;
}

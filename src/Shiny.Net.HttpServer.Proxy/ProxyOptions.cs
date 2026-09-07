using System.Net;

namespace Shiny.Net.HttpServer.Proxy;

/// <summary>Why a proxied request did not come back with the upstream's answer.</summary>
public enum ProxyError
{
    /// <summary>Nothing went wrong.</summary>
    None = 0,

    /// <summary>The upstream could not be reached, or answered something unparseable. A 502.</summary>
    Request,

    /// <summary>The upstream did not answer in time. A 504.</summary>
    RequestTimeout,

    /// <summary>The caller went away. Nothing is sent, because there is nobody to send it to.</summary>
    RequestCanceled,

    /// <summary>The upstream's answer started arriving and then stopped. The connection is aborted.</summary>
    ResponseBody,

    /// <summary>An upgrade was requested and the upstream refused or the switch failed.</summary>
    Upgrade,

    /// <summary>Every destination in the cluster is out of rotation. A 503.</summary>
    NoAvailableDestination
}

/// <summary>Where a proxied route sends the request, and what it does to it on the way.</summary>
public sealed class ProxyOptions
{
    public ProxyOptions() : this(new TransformBuilder())
    {
    }

    internal ProxyOptions(TransformBuilder transforms) => this.Transforms = transforms;

    /// <summary>
    /// The client used for the outbound call. One is created when this is null — redirects off,
    /// cookies off, which is what a forwarder wants: following a redirect on the caller's behalf
    /// hides it from them, and a shared cookie container mixes callers together.
    /// </summary>
    public HttpMessageInvoker? Client { get; set; }

    /// <summary>How long the upstream has to answer. Exceeding it is a 504.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);

    /// <summary>
    /// Sends <c>X-Forwarded-For</c>, <c>-Proto</c> and <c>-Host</c> describing the original caller.
    /// On by default: without them the upstream sees this server and nothing else.
    /// <para>
    /// A shorthand for <see cref="TransformBuilder.ForwardedHeaders"/>, which also offers
    /// <see cref="ForwardedHeadersMode.Append"/> for a deliberate chain of proxies.
    /// </para>
    /// </summary>
    public bool AddForwardedHeaders
    {
        get => this.Transforms.ForwardedHeaders != ForwardedHeadersMode.Off;
        set => this.Transforms.ForwardedHeaders = value ? ForwardedHeadersMode.Set : ForwardedHeadersMode.Off;
    }

    /// <summary>
    /// Sets the outbound <c>Host</c> to the destination's. On by default, because most upstreams
    /// route on it. Turn it off to pass the caller's host through — a virtual host that has to see
    /// the original name.
    /// </summary>
    public bool RewriteHost { get; set; } = true;

    /// <summary>
    /// Forwards a WebSocket handshake, or any other <c>Upgrade</c>, by taking the connection over
    /// and pumping bytes in both directions once the upstream has answered 101.
    /// <para>
    /// On by default. It only applies to HTTP/1.1 connections, which is the only protocol version
    /// an upgrade exists in.
    /// </para>
    /// </summary>
    public bool ForwardUpgrades { get; set; } = true;

    /// <summary>HTTP version used for the outbound call. Upgrades always use 1.1 whatever this says.</summary>
    public Version RequestVersion { get; set; } = HttpVersion.Version11;

    /// <summary>How strictly <see cref="RequestVersion"/> is applied.</summary>
    public HttpVersionPolicy VersionPolicy { get; set; } = HttpVersionPolicy.RequestVersionOrLower;

    /// <summary>Builds the upstream URI. Replaces the default "destination + the catch-all remainder".</summary>
    public Func<HttpContext, Uri>? RewriteUri { get; set; }

    /// <summary>Last chance to touch the outbound request — add an upstream API key, drop a header.</summary>
    public Action<HttpRequestMessage, HttpContext>? BeforeSend { get; set; }

    /// <summary>First look at the upstream response, before it is copied back.</summary>
    public Action<HttpResponseMessage, HttpContext>? AfterReceive { get; set; }

    /// <summary>
    /// The transform pipeline. Everything <see cref="BeforeSend"/> and <see cref="AfterReceive"/>
    /// can do, plus path and query rewriting, and composable rather than one callback per route.
    /// </summary>
    public TransformBuilder Transforms { get; }

    /// <summary>
    /// Called instead of the default status code when forwarding fails, for a proxy that answers in
    /// its own shape — a problem-details body, a cached copy, a friendlier page.
    /// </summary>
    public Func<HttpContext, ProxyError, Exception?, ValueTask>? OnError { get; set; }

    /// <summary>
    /// The same settings with a different transform pipeline. Configuration puts transforms on the
    /// route and everything else on the cluster, and a route has to end up holding one object that
    /// carries both.
    /// </summary>
    internal ProxyOptions With(TransformBuilder transforms) => new(transforms)
    {
        Client = this.Client,
        Timeout = this.Timeout,
        RewriteHost = this.RewriteHost,
        ForwardUpgrades = this.ForwardUpgrades,
        RequestVersion = this.RequestVersion,
        VersionPolicy = this.VersionPolicy,
        RewriteUri = this.RewriteUri,
        BeforeSend = this.BeforeSend,
        AfterReceive = this.AfterReceive,
        OnError = this.OnError
    };
}

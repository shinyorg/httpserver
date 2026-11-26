using System.Net;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer;

/// <summary>
/// Who is believed when <see cref="HttpServerOptions.UseForwardedHeaders"/> is on, and how far back
/// through a chain of proxies.
/// <para>
/// Each proxy <em>appends</em> the address it received the request from to <c>X-Forwarded-For</c>, so the
/// header reads left to right from whatever the client claimed to the hop nearest this server. Only the
/// right-hand end was written by someone trusted: the left-most entry is whatever the client chose to send
/// and is the one entry never to believe. Entries are therefore read from the right, one hop per trusted
/// proxy, up to <see cref="ForwardLimit"/>.
/// </para>
/// <code>
/// var options = new HttpServerOptions { UseForwardedHeaders = true };
/// options.ForwardedHeaders.KnownNetworks.Add(IpAddressRange.Parse("10.0.0.0/8"));   // the load balancers
/// options.ForwardedHeaders.ForwardLimit = 2;                                        // CDN → LB → this server
/// </code>
/// </summary>
public sealed class ForwardedHeadersOptions
{
    /// <summary>
    /// Proxy addresses whose forwarded headers are believed. With both this and
    /// <see cref="KnownNetworks"/> empty (the default), every peer is believed — the server must then only be
    /// reachable through proxies that overwrite or append to the headers.
    /// </summary>
    public IList<IPAddress> KnownProxies { get; } = [];

    /// <summary>Proxy networks whose forwarded headers are believed. See <see cref="KnownProxies"/>.</summary>
    public IList<IpAddressRange> KnownNetworks { get; } = [];

    /// <summary>
    /// How many entries, counted from the right, may be consumed — one per proxy between the client and
    /// this server. 1 (the default) takes the address the nearest proxy saw and nothing a client wrote
    /// before it. Raise it for a chain, and list the inner proxies in <see cref="KnownProxies"/> or
    /// <see cref="KnownNetworks"/> so the walk stops at the first address that is not one of them. Null
    /// removes the limit.
    /// </summary>
    public int? ForwardLimit { get; set; } = 1;

    /// <summary>True when the request may come from <paramref name="address"/>'s forwarded headers.</summary>
    internal bool IsTrusted(IPAddress? address)
    {
        if (this.KnownProxies.Count == 0 && this.KnownNetworks.Count == 0)
            return true;

        if (address is null)
            return false;

        var normalized = IpAddressRange.Normalize(address);

        return this.KnownProxies.Any(p => IpAddressRange.Normalize(p).Equals(normalized))
            || this.KnownNetworks.Any(n => n.Contains(normalized));
    }
}

/// <summary>Applies <c>X-Forwarded-For</c> / <c>-Proto</c> / <c>-Host</c> to a request, right to left.</summary>
static class ForwardedHeaders
{
    /// <summary>
    /// Rewrites the client address, scheme and host from the forwarded headers, believing only as many
    /// hops from the right as trusted proxies vouch for. Nothing changes when the immediate peer is not
    /// trusted.
    /// </summary>
    public static void Apply(HttpContext context, ForwardedHeadersOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        var request = context.Request;
        var peer = context.Connection.RemoteIpAddress;

        if (!options.IsTrusted(peer))
            return;

        var forwardedFor = Split(request.Headers.GetFirst(HeaderNames.XForwardedFor));
        var proto = Split(request.Headers.GetFirst(HeaderNames.XForwardedProto));
        var host = Split(request.Headers.GetFirst(HeaderNames.XForwardedHost));

        var limit = options.ForwardLimit ?? int.MaxValue;
        var depth = 0;
        IPAddress? client = null;

        // Walk from the right. Each step needs the address we currently believe — the socket peer at
        // first — to be a trusted proxy, since that is who wrote the entry being read.
        for (var i = forwardedFor.Length - 1; i >= 0 && depth < limit && options.IsTrusted(client ?? peer); i--)
        {
            if (!TryParseAddress(forwardedFor[i], out var address))
                break;

            client = address;
            depth++;
        }

        // A proxy that sets only the scheme or host is still one hop.
        if (depth == 0 && forwardedFor.Length == 0 && limit > 0)
            depth = 1;

        if (depth == 0)
            return;

        if (client is not null)
            context.Connection.RemoteIpAddress = client;

        // The scheme and host lists line up with the address list hop for hop, from the right.
        if (Pick(proto, depth) is { Length: > 0 } scheme)
            request.Scheme = scheme;

        if (Pick(host, depth) is { Length: > 0 } forwardedHost)
            request.Host = forwardedHost;
    }

    /// <summary>The right-most entry a client could not have written: the one nearest this server.</summary>
    public static IPAddress? RightMostForwardedFor(HttpContext context)
    {
        var entries = Split(context.Request.Headers.GetFirst(HeaderNames.XForwardedFor));
        return entries.Length > 0 && TryParseAddress(entries[^1], out var address) ? address : null;
    }

    static string? Pick(string[] values, int depth)
        => values.Length == 0 ? null : values[Math.Max(0, values.Length - depth)];

    static string[] Split(string? header)
        => string.IsNullOrEmpty(header)
            ? []
            : header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static bool TryParseAddress(string entry, out IPAddress address)
    {
        var span = entry.AsSpan().Trim();

        // [v6]:port, then v4:port; a bare IPv6 literal is full of colons and keeps them.
        if (span.StartsWith("[") && span.IndexOf(']') is var close and > 0)
            span = span[1..close];
        else if (span.Count(':') == 1)
            span = span[..span.IndexOf(':')];

        if (IPAddress.TryParse(span, out var parsed))
        {
            address = parsed;
            return true;
        }

        address = IPAddress.None;
        return false;
    }
}

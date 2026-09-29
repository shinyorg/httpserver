using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer.Switchboard.Client;

/// <summary>
/// Builds a <see cref="SwitchboardLine"/>.
/// <code>
/// var line = new SwitchboardLineBuilder()
///     .WithUrl("https://device.local:5001/chat", o => o.AccessTokenProvider = () => auth.GetTokenAsync())
///     .WithJson(ChatJsonContext.Default)
///     .WithAutomaticReconnect()
///     .Build();
/// </code>
/// </summary>
public sealed class SwitchboardLineBuilder
{
    readonly List<IJsonTypeInfoResolver> resolvers = [];
    Uri? url;
    SwitchboardLineOptions options = new();
    IRetryPolicy? retryPolicy;
    ILoggerFactory? loggerFactory;

    /// <summary>The switchboard's base URL — the path it was mapped at, without <c>/connect</c>.</summary>
    public SwitchboardLineBuilder WithUrl(string url, Action<SwitchboardLineOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return this.WithUrl(new Uri(url, UriKind.Absolute), configure);
    }

    public SwitchboardLineBuilder WithUrl(Uri url, Action<SwitchboardLineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(url);

        this.url = url;
        configure?.Invoke(this.options);
        return this;
    }

    /// <summary>
    /// Adds JSON metadata for the types sent and received — the same context the server uses, ideally,
    /// so both ends agree on the shape of every message. Call once per context.
    /// </summary>
    public SwitchboardLineBuilder WithJson(IJsonTypeInfoResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        this.resolvers.Add(resolver);
        return this;
    }

    /// <summary>Reconnects after a drop: 0, 2, 10 and 30 seconds, then gives up.</summary>
    public SwitchboardLineBuilder WithAutomaticReconnect() => this.WithAutomaticReconnect(new DefaultRetryPolicy());

    /// <summary>Reconnects after a drop, waiting each of <paramref name="delays"/> in turn.</summary>
    public SwitchboardLineBuilder WithAutomaticReconnect(params TimeSpan[] delays)
        => this.WithAutomaticReconnect(new DefaultRetryPolicy(delays));

    public SwitchboardLineBuilder WithAutomaticReconnect(IRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        this.retryPolicy = policy;
        return this;
    }

    public SwitchboardLineBuilder WithLoggerFactory(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        this.loggerFactory = loggerFactory;
        return this;
    }

    public SwitchboardLine Build()
    {
        if (this.url is null)
            throw new InvalidOperationException("Call WithUrl(...) before Build().");

        return new SwitchboardLine(this.url, this.options, [.. this.resolvers], this.retryPolicy, this.loggerFactory);
    }
}

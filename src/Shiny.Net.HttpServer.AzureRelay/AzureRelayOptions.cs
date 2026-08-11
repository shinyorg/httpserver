using Microsoft.Azure.Relay;

namespace Shiny.Net.HttpServer.AzureRelay;

/// <summary>How traffic reaches the device through the relay.</summary>
public enum AzureRelayMode
{
    /// <summary>
    /// Azure Relay terminates HTTPS and forwards each request. Anything that speaks HTTP —
    /// a browser, curl, a webhook — can reach the device at
    /// <c>https://{namespace}/{hybridConnectionName}</c> with no client library at all.
    /// </summary>
    Http,

    /// <summary>
    /// Callers open a relayed byte stream with <c>HybridConnectionClient</c>, and the server sees
    /// it as an ordinary connection.
    /// <para>
    /// More faithful than <see cref="Http"/> — keep-alive, streaming and WebSocket upgrades all
    /// work, because nothing in between is parsing the traffic — but the caller has to be an Azure
    /// Relay client rather than a plain HTTP one.
    /// </para>
    /// </summary>
    RelayedStream
}

/// <summary>
/// How to reach an Azure Relay hybrid connection.
/// <para>
/// Give it either a connection string (what the portal hands you) or the namespace and credentials
/// separately. On a device, prefer <see cref="SharedAccessSignature"/> — see the remarks.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// A connection string contains the shared access key, and a key shipped inside a mobile app is a
/// key you have published. Anyone who extracts it can listen on your hybrid connection and
/// impersonate the device.
/// </para>
/// <para>
/// The safer shape is for your backend to mint a short-lived SAS token (it holds the key) and hand
/// it to the device, which passes it as <see cref="SharedAccessSignature"/>. Use
/// <see cref="AzureRelaySas.Create"/> on the backend. Set <see cref="RefreshSharedAccessSignature"/>
/// so the device can fetch a fresh one before the old expires.
/// </para>
/// </remarks>
public sealed class AzureRelayOptions
{
    /// <summary>
    /// The namespace-level or entity-level connection string from the portal. When it carries an
    /// <c>EntityPath</c>, <see cref="HybridConnectionName"/> is not needed.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>The hybrid connection name, e.g. <c>my-device</c>.</summary>
    public string? HybridConnectionName { get; set; }

    /// <summary>The relay namespace host, e.g. <c>my-namespace.servicebus.windows.net</c>.</summary>
    public string? Namespace { get; set; }

    /// <summary>SAS policy name, e.g. <c>listen-only</c>. Used with <see cref="SharedAccessKey"/>.</summary>
    public string? SharedAccessKeyName { get; set; }

    /// <summary>The SAS policy key. See the remarks on this type before putting one in an app.</summary>
    public string? SharedAccessKey { get; set; }

    /// <summary>
    /// A pre-issued SAS token (<c>SharedAccessSignature sr=...&amp;sig=...&amp;se=...&amp;skn=...</c>),
    /// which lets a device listen without ever holding the key.
    /// </summary>
    public string? SharedAccessSignature { get; set; }

    /// <summary>
    /// Called to obtain a fresh SAS token when the connection is (re)established. Set this and the
    /// device never has to hold a long-lived credential.
    /// </summary>
    public Func<CancellationToken, Task<string>>? RefreshSharedAccessSignature { get; set; }

    public AzureRelayMode Mode { get; set; } = AzureRelayMode.Http;

    /// <summary>
    /// How often the listener pings the relay to keep the control connection alive.
    /// <para>
    /// Left null, the SDK's default applies. On cellular, longer is kinder: every ping wakes the
    /// radio, and reconnecting after an idle timeout costs far less battery than never going idle.
    /// </para>
    /// </summary>
    public TimeSpan? KeepAliveInterval { get; set; }

    /// <summary>
    /// Strips the hybrid connection name from the front of the path, so a handler mapped at
    /// <c>/api/widgets</c> is reachable relayed and locally at the same route.
    /// <para>
    /// Azure Relay addresses a device as <c>https://{namespace}/{name}/{path}</c>, so without this
    /// every route would need the connection name baked into it.
    /// </para>
    /// </summary>
    public bool StripHybridConnectionNameFromPath { get; set; } = true;

    /// <summary>
    /// Decides whether to accept an incoming rendezvous. Return false to reject.
    /// <para>
    /// Only consulted in <see cref="AzureRelayMode.RelayedStream"/>; in HTTP mode, authorize in the
    /// pipeline like any other request.
    /// </para>
    /// </summary>
    public Func<RelayedHttpListenerContext, Task<bool>>? Authorize { get; set; }

    /// <summary>Scheme used when reporting <c>PublicUrl</c>. Only <c>https</c> makes sense in practice.</summary>
    public string PublicScheme { get; set; } = "https";

    /// <summary>Largest response head this provider will buffer while relaying an HTTP response.</summary>
    public int MaxResponseHeadSize { get; set; } = 64 * 1024;

    /// <summary>Validates the options and resolves the namespace and connection name.</summary>
    internal (string Namespace, string Name) Resolve()
    {
        if (this.ConnectionString is { Length: > 0 } connectionString)
        {
            var builder = new RelayConnectionStringBuilder(connectionString);
            var name = this.HybridConnectionName ?? builder.EntityPath;

            if (string.IsNullOrEmpty(name))
                throw new InvalidOperationException(
                    $"The connection string has no EntityPath, so {nameof(this.HybridConnectionName)} must be set."
                );

            if (builder.Endpoint is null)
                throw new InvalidOperationException("The connection string has no Endpoint.");

            return (builder.Endpoint.Host, name);
        }

        if (string.IsNullOrEmpty(this.Namespace))
            throw new InvalidOperationException(
                $"Set either {nameof(this.ConnectionString)} or {nameof(this.Namespace)}."
            );

        if (string.IsNullOrEmpty(this.HybridConnectionName))
            throw new InvalidOperationException($"{nameof(this.HybridConnectionName)} is required.");

        var hasKey = !string.IsNullOrEmpty(this.SharedAccessKeyName) && !string.IsNullOrEmpty(this.SharedAccessKey);
        var hasToken = !string.IsNullOrEmpty(this.SharedAccessSignature) || this.RefreshSharedAccessSignature is not null;

        if (!hasKey && !hasToken)
            throw new InvalidOperationException(
                "No credentials. Set SharedAccessKeyName + SharedAccessKey, or SharedAccessSignature, " +
                "or RefreshSharedAccessSignature."
            );

        return (this.Namespace!, this.HybridConnectionName!);
    }

    /// <summary>Builds the listener these options describe.</summary>
    internal async Task<HybridConnectionListener> CreateListenerAsync(CancellationToken cancellationToken)
    {
        var (host, name) = this.Resolve();

        HybridConnectionListener listener;

        if (this.RefreshSharedAccessSignature is { } refresh)
        {
            var token = await refresh(cancellationToken).ConfigureAwait(false);
            listener = new HybridConnectionListener(
                new Uri($"sb://{host}/{name}"),
                TokenProvider.CreateSharedAccessSignatureTokenProvider(token)
            );
        }
        else if (this.SharedAccessSignature is { Length: > 0 } signature)
        {
            listener = new HybridConnectionListener(
                new Uri($"sb://{host}/{name}"),
                TokenProvider.CreateSharedAccessSignatureTokenProvider(signature)
            );
        }
        else if (this.ConnectionString is { Length: > 0 } connectionString)
        {
            listener = this.HybridConnectionName is { Length: > 0 } explicitName
                ? new HybridConnectionListener(connectionString, explicitName)
                : new HybridConnectionListener(connectionString);
        }
        else
        {
            listener = new HybridConnectionListener(
                new Uri($"sb://{host}/{name}"),
                TokenProvider.CreateSharedAccessSignatureTokenProvider(this.SharedAccessKeyName, this.SharedAccessKey)
            );
        }

        if (this.KeepAliveInterval is { } interval)
            listener.KeepAliveInterval = interval;

        if (this.Authorize is { } authorize)
            listener.AcceptHandler = authorize;

        return listener;
    }
}

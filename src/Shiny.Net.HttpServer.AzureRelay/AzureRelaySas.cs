using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Shiny.Net.HttpServer.AzureRelay;

/// <summary>
/// Mints Azure Relay shared access signature tokens.
/// <para>
/// Meant for the <em>backend</em>. The key stays on a server you control, and each device gets a
/// short-lived token scoped to its own hybrid connection — so a compromised device leaks something
/// that expires, not the key to the whole namespace.
/// </para>
/// </summary>
/// <example>
/// On the backend, in response to an authenticated device asking for a token:
/// <code>
/// var token = AzureRelaySas.Create(
///     "my-namespace.servicebus.windows.net",
///     hybridConnectionName: deviceId,
///     keyName: "device-listen",
///     key: configuration["Relay:ListenKey"]!,
///     validFor: TimeSpan.FromHours(8)
/// );
/// </code>
/// On the device:
/// <code>
/// var options = new AzureRelayOptions
/// {
///     Namespace = "my-namespace.servicebus.windows.net",
///     HybridConnectionName = deviceId,
///     RefreshSharedAccessSignature = ct => api.GetRelayTokenAsync(ct)
/// };
/// </code>
/// </example>
public static class AzureRelaySas
{
    /// <summary>
    /// Creates a token for one hybrid connection.
    /// </summary>
    /// <param name="relayNamespace">Namespace host, e.g. <c>my-namespace.servicebus.windows.net</c>.</param>
    /// <param name="hybridConnectionName">The hybrid connection the token grants access to.</param>
    /// <param name="keyName">The SAS policy name, e.g. <c>device-listen</c>.</param>
    /// <param name="key">The SAS policy key.</param>
    /// <param name="validFor">
    /// How long the token lasts. Keep it short — hours, not months. The device refreshes through
    /// <see cref="AzureRelayOptions.RefreshSharedAccessSignature"/>.
    /// </param>
    public static string Create(
        string relayNamespace,
        string hybridConnectionName,
        string keyName,
        string key,
        TimeSpan validFor
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relayNamespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(hybridConnectionName);

        var resource = $"http://{relayNamespace}/{hybridConnectionName.TrimStart('/')}";

        return CreateForResource(resource, keyName, key, validFor);
    }

    /// <summary>
    /// Creates a token for an arbitrary relay resource URI, for the rare case where the scope is
    /// not one hybrid connection (a whole namespace, say).
    /// </summary>
    public static string CreateForResource(string resourceUri, string keyName, string key, TimeSpan validFor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (validFor <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(validFor), "The token would already have expired.");

        // Service Bus and Relay both sign the *encoded* resource, lowercased, so a token signed
        // over a differently-cased or unencoded URI is rejected with a bare 401.
        var encodedResource = Uri.EscapeDataString(resourceUri.ToLowerInvariant());
        var expiry = DateTimeOffset.UtcNow.Add(validFor).ToUnixTimeSeconds();
        var expiryText = expiry.ToString(CultureInfo.InvariantCulture);

        var signature = Convert.ToBase64String(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes($"{encodedResource}\n{expiryText}"))
        );

        return string.Create(
            CultureInfo.InvariantCulture,
            $"SharedAccessSignature sr={encodedResource}&sig={Uri.EscapeDataString(signature)}&se={expiryText}&skn={Uri.EscapeDataString(keyName)}"
        );
    }

    /// <summary>
    /// When the token expires, so a device can refresh before it does. Returns null if the token is
    /// not a well-formed SAS token.
    /// </summary>
    public static DateTimeOffset? GetExpiry(string sharedAccessSignature)
    {
        if (string.IsNullOrWhiteSpace(sharedAccessSignature))
            return null;

        const string prefix = "SharedAccessSignature ";
        var fields = sharedAccessSignature.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? sharedAccessSignature[prefix.Length..]
            : sharedAccessSignature;

        foreach (var part in fields.Split('&'))
        {
            if (!part.StartsWith("se=", StringComparison.OrdinalIgnoreCase))
                continue;

            return long.TryParse(part.AsSpan(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }

        return null;
    }
}

using System.Diagnostics;

namespace Shiny.Net.HttpServer.OAuthLoopback;

/// <summary>
/// Opens a URI in the user's default browser — the other half of a loopback sign-in. Desktop only:
/// iOS, tvOS and Android cannot keep a loopback server alive while the browser is in front, and use
/// <c>WebAuthenticator</c> / <c>ASWebAuthenticationSession</c> with a custom scheme instead.
/// </summary>
public static class SystemBrowser
{
    /// <summary>True where <see cref="Open"/> can work.</summary>
    public static bool IsSupported
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
            || OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD();

    /// <summary>Opens <paramref name="uri"/> in the default browser.</summary>
    /// <exception cref="PlatformNotSupportedException">On a mobile or TV platform.</exception>
    public static void Open(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("Only absolute http and https URIs are opened.", nameof(uri));

        // Android reports IsLinux() as well, so it has to be excluded by name.
        if (!IsSupported || OperatingSystem.IsAndroid())
        {
            throw new PlatformNotSupportedException(
                "A loopback sign-in needs a desktop browser. On iOS, tvOS and Android use WebAuthenticator " +
                "(ASWebAuthenticationSession / Custom Tabs) with a custom URI scheme instead."
            );
        }

        var url = uri.AbsoluteUri;
        ProcessStartInfo startInfo;

        if (OperatingSystem.IsWindows())
        {
            // The shell resolves the registered handler for the scheme. Going through cmd /c start
            // instead would need & in the query string escaped, and that is exactly where OAuth
            // puts its parameters.
            startInfo = new ProcessStartInfo(url) { UseShellExecute = true };
        }
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            startInfo = new ProcessStartInfo("open") { UseShellExecute = false };
            startInfo.ArgumentList.Add(url);
        }
        else
        {
            startInfo = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            startInfo.ArgumentList.Add(url);
        }

        using var _ = Process.Start(startInfo);
    }
}

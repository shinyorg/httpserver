namespace Shiny.Net.HttpServer.Mobile;

/// <summary>
/// The part of the Apple bundle check that does not need a bundle.
/// </summary>
/// <remarks>
/// Outside the <c>PLATFORM</c> guard, so it compiles - and is tested - on the base target framework,
/// the same arrangement <see cref="ConnectivityRebindDecision"/> uses and for the same reason. The
/// platform half reads <c>NSBundle</c> and hands the values here; everything that decides what those
/// values mean lives in one place that a test can reach.
/// </remarks>
static class AppleLocalNetworkRequirements
{
    /// <summary>
    /// Turns what the bundle declares into the problems and notes a person can act on.
    /// </summary>
    /// <param name="localNetworkUsageDescription">
    /// <c>NSLocalNetworkUsageDescription</c> as declared, or null when the key is absent.
    /// </param>
    /// <param name="declaresBonjourServices">Whether <c>NSBonjourServices</c> is present and non-empty.</param>
    /// <param name="isMacCatalyst">Whether the app is running as a Mac Catalyst app, which is sandboxed.</param>
    /// <param name="problems">Collects what will stop the server being reachable.</param>
    /// <param name="notes">Collects what is worth knowing but is not an error.</param>
    public static void Evaluate(
        string? localNetworkUsageDescription,
        bool declaresBonjourServices,
        bool isMacCatalyst,
        List<string> problems,
        List<string> notes
    )
    {
        // Whitespace counts as absent rather than present. The key is a sentence shown to the user in
        // the permission prompt, and an empty one is both useless to them and a review rejection - so
        // treating it as declared would be the check passing on exactly the bundle it exists to catch.
        if (String.IsNullOrWhiteSpace(localNetworkUsageDescription))
        {
            problems.Add(
                "The bundle does not declare NSLocalNetworkUsageDescription. Serving on the local " +
                "network is gated behind it just as much as connecting out is, and without the key " +
                "the app is denied without the user ever being asked. Add it to Info.plist with a " +
                "sentence describing what the server is for."
            );
        }

        if (!declaresBonjourServices)
        {
            notes.Add(
                "The bundle does not declare NSBonjourServices. Only mDNS needs it - " +
                "Shiny.Net.HttpServer.Discovery browsing for other devices will find nothing without " +
                "the service types listed there. Serving does not need it."
            );
        }

        if (isMacCatalyst)
        {
            // The entitlement is not in Info.plist and is not readable from inside the process without
            // reaching into SecTask, which is a lot of interop for a helper whose whole job is to be
            // cheap and never throw. Said as a note rather than guessed at: this check cannot see it.
            notes.Add(
                "Mac Catalyst runs sandboxed and this check cannot read entitlements, so it cannot " +
                "confirm com.apple.security.network.server is set. Without that entitlement the bind " +
                "is refused and the server simply never appears."
            );
        }
    }
}

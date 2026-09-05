namespace Shiny.Net.HttpServer.Mobile;

/// <summary>What the platform will and will not let this app do on the local network.</summary>
/// <param name="CanServe">False when something in the app's configuration will stop the bind from working.</param>
/// <param name="Problems">What is missing, in the words of whoever has to fix it.</param>
/// <param name="Notes">Things worth knowing that are not errors.</param>
public sealed record LocalNetworkReport(bool CanServe, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes)
{
    public override string ToString()
        => this.CanServe
            ? "Local network serving looks configured." + (this.Notes.Count > 0 ? " " + string.Join(" ", this.Notes) : "")
            : string.Join(" ", this.Problems);
}

/// <summary>
/// Checks the app's own configuration for the things that silently stop an embedded server working
/// on a device.
/// <para>
/// This exists because of how these failures present. A missing
/// <c>NSLocalNetworkUsageDescription</c> does not raise an exception with that name in it — the
/// listener binds, the other device cannot reach it, and there is nothing in the log. A missing
/// Android foreground-service permission crashes somewhere inside the platform. Neither is
/// discoverable from the failure, and both are discoverable from the bundle, so this reads the
/// bundle and says so in a sentence a person can act on.
/// </para>
/// <code>
/// var report = LocalNetworkAccess.Check();
/// if (!report.CanServe)
///     logger.LogWarning("{Report}", report);
/// </code>
/// </summary>
public static partial class LocalNetworkAccess
{
    /// <summary>
    /// Inspects the app's manifest or bundle. Cheap, synchronous, and safe to call at startup.
    /// <para>
    /// It does not prompt for anything and it cannot tell you whether the user has already granted
    /// local network access — no platform exposes that. What it can tell you is whether the app was
    /// built in a way that makes granting it possible.
    /// </para>
    /// </summary>
    public static LocalNetworkReport Check()
    {
        var problems = new List<string>();
        var notes = new List<string>();

        CheckPlatform(problems, notes);

        return new LocalNetworkReport(problems.Count == 0, problems, notes);
    }

    /// <summary>
    /// Implemented once per target framework, in <c>Platforms/</c> for the ones that have a bundle or
    /// a manifest to read and immediately below for the ones that do not.
    /// </summary>
    /// <remarks>
    /// The <c>private</c> is load-bearing and is the reason this comment exists. Without an
    /// accessibility modifier this is an ordinary partial method, and an ordinary partial method with
    /// no implementation compiles away to nothing without so much as a warning — which is precisely
    /// how every Apple target shipped for as long as it did reporting "looks configured" whatever the
    /// bundle actually said, while the documentation told people to trust the answer. Spelling the
    /// modifier out makes this an extended partial method, and an extended partial method without an
    /// implementation is a compile error (CS8795). A target framework added later cannot quietly
    /// inherit the silent no-op; it either gets an implementation or it does not build.
    /// </remarks>
    private static partial void CheckPlatform(List<string> problems, List<string> notes);

#if !PLATFORM
    /// <summary>
    /// Nothing to check. A process on a desktop or a server binds a port because it asked to,
    /// with no bundle and no manifest standing between the two, so there is no configuration here that
    /// can be missing and an empty report is the honest one.
    /// </summary>
    private static partial void CheckPlatform(List<string> problems, List<string> notes)
    {
    }
#endif
}

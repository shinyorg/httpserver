using Foundation;

namespace Shiny.Net.HttpServer.Mobile;

public static partial class LocalNetworkAccess
{
    private static partial void CheckPlatform(List<string> problems, List<string> notes)
        => AppleLocalNetworkRequirements.Evaluate(
            InfoValue("NSLocalNetworkUsageDescription") is NSString description ? description.ToString() : null,
            // Declared as an array of service types. A present-but-empty array is what a template
            // leaves behind and it advertises nothing, so it does not count as declared.
            InfoValue("NSBonjourServices") is NSArray { Count: > 0 },
            OperatingSystem.IsMacCatalyst(),
            problems,
            notes
        );

    static NSObject? InfoValue(string key)
    {
        try
        {
            return NSBundle.MainBundle.ObjectForInfoDictionary(key);
        }
        catch (Exception)
        {
            // Matching the Android half: a bundle that will not answer is not evidence of a missing
            // key, and a diagnostic helper is the last thing that should throw.
            return null;
        }
    }
}

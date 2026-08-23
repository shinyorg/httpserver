using Android.Content.PM;
using Application = Android.App.Application;

namespace Shiny.Net.HttpServer.Mobile;

public static partial class LocalNetworkAccess
{
    static partial void CheckPlatform(List<string> problems, List<string> notes)
    {
        var context = Application.Context;
        var declared = DeclaredPermissions(context);

        if (!declared.Contains(Android.Manifest.Permission.Internet))
        {
            problems.Add(
                "The manifest does not declare android.permission.INTERNET. Binding a listener needs " +
                "it just as much as making a request does."
            );
        }

        // The literal rather than the constant: the constant carries an [SupportedOSPlatform("28")]
        // annotation, and reading a manifest is not a call that needs one.
        if (!declared.Contains("android.permission.FOREGROUND_SERVICE"))
        {
            notes.Add(
                "The manifest does not declare FOREGROUND_SERVICE. BackgroundServerMode.KeepAlive " +
                "needs it, along with FOREGROUND_SERVICE_DATA_SYNC on API 34+."
            );
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && !declared.Contains("android.permission.POST_NOTIFICATIONS"))
        {
            notes.Add(
                "The manifest does not declare POST_NOTIFICATIONS. From API 33 the user must grant it " +
                "for the foreground service's notification to appear."
            );
        }
    }

    static HashSet<string> DeclaredPermissions(Android.Content.Context context)
    {
        try
        {
            var info = context.PackageManager?.GetPackageInfo(context.PackageName!, PackageInfoFlags.Permissions);

            return info?.RequestedPermissions is { } permissions
                ? [.. permissions]
                : [];
        }
        catch (Exception)
        {
            // A package manager that will not answer is not evidence of a missing permission, and
            // a diagnostic helper is the last thing that should throw.
            return [];
        }
    }
}

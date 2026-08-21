namespace Shiny.Net.HttpServer.CommandLine;

/// <summary>
/// What the mount is allowed to do. Read is always on - a server that cannot
/// be read from is not one worth starting.
/// </summary>
[Flags]
public enum Permissions
{
    Read = 1,
    Create = 2,
    Update = 4,
    Delete = 8
}


public static class PermissionsExtensions
{
    public static bool Has(this Permissions permissions, Permissions flag)
        => (permissions & flag) == flag;

    /// <summary>Anything that changes the disk. Drives the "you are exposed" warning.</summary>
    public static bool AllowsChanges(this Permissions permissions)
        => permissions.Has(Permissions.Create) ||
           permissions.Has(Permissions.Update) ||
           permissions.Has(Permissions.Delete);

    public static string Describe(this Permissions permissions)
        => String.Join(
            ", ",
            Enum.GetValues<Permissions>()
                .Where(x => permissions.Has(x))
                .Select(x => x.ToString().ToLowerInvariant())
        );
}

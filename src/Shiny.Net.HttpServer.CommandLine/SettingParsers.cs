using System.Net;

namespace Shiny.Net.HttpServer.CommandLine;


/// <summary>
/// The text forms a setting can be typed in, read once for both places that take typing: the
/// command line and the dashboard's settings form. Each returns the error it would show rather than
/// showing it, so the two can put it wherever they put errors.
/// </summary>
public static class SettingParsers
{
    public const long DefaultMaxUpload = 64 * 1024 * 1024;


    public static bool TryParseAddress(string value, out IPAddress address, out string? error)
    {
        error = null;
        switch (value.Trim().ToLowerInvariant())
        {
            case "any":
            case "all":
                address = IPAddress.Any;
                return true;

            case "localhost":
            case "loopback":
                address = IPAddress.Loopback;
                return true;
        }

        if (IPAddress.TryParse(value.Trim(), out var parsed))
        {
            address = parsed;
            return true;
        }

        address = IPAddress.Loopback;
        error = $"'{value}' is not an IP address. Use an IP, 'any' or 'localhost'.";
        return false;
    }


    /// <summary>The way an address reads back: the names the parser takes, rather than 0.0.0.0.</summary>
    public static string FormatAddress(IPAddress address)
    {
        if (address.Equals(IPAddress.Any))
            return "any";

        if (address.Equals(IPAddress.Loopback))
            return "localhost";

        return address.ToString();
    }


    /// <summary>Folds one operation name into <paramref name="permissions"/>, or says why it cannot.</summary>
    public static bool TryAddPermission(string value, ref Permissions permissions, out string? error)
    {
        error = null;
        switch (value.Trim().ToLowerInvariant())
        {
            case "read":
                return true;

            case "create":
                permissions |= Permissions.Create;
                return true;

            case "update":
                permissions |= Permissions.Update;
                return true;

            case "delete":
                permissions |= Permissions.Delete;
                return true;

            case "all":
                permissions |= Permissions.Create | Permissions.Update | Permissions.Delete;
                return true;

            default:
                error = $"'{value}' is not an operation. Use read, create, update, delete or all.";
                return false;
        }
    }


    public static bool TryParseUser(string value, out BasicUser? user, out string? error)
    {
        var index = value.IndexOf(':');
        if (index < 1 || index == value.Length - 1)
        {
            user = null;
            error = $"'{value}' is not a credential. Use user:password.";
            return false;
        }

        user = new BasicUser(value[..index], value[(index + 1)..]);
        error = null;
        return true;
    }


    public static bool TryParseSize(string value, out long bytes, out string? error)
    {
        var text = value.Trim().ToLowerInvariant();
        var multiplier = 1L;

        foreach (var (suffix, scale) in new[] { ("gb", 1024L * 1024 * 1024), ("mb", 1024L * 1024), ("kb", 1024L), ("g", 1024L * 1024 * 1024), ("m", 1024L * 1024), ("k", 1024L), ("b", 1L) })
        {
            if (text.EndsWith(suffix))
            {
                multiplier = scale;
                text = text[..^suffix.Length].Trim();
                break;
            }
        }

        if (Int64.TryParse(text, out var number) && number > 0)
        {
            bytes = number * multiplier;
            error = null;
            return true;
        }

        bytes = DefaultMaxUpload;
        error = $"'{value}' is not a size. Use bytes or a suffix like 500k, 64mb, 2gb.";
        return false;
    }


    /// <summary>
    /// A size the way someone would type it back into <see cref="TryParseSize"/> - whole units only,
    /// so what the form shows is exactly what it holds.
    /// </summary>
    public static string FormatSize(long bytes)
    {
        foreach (var (suffix, scale) in new[] { ("gb", 1024L * 1024 * 1024), ("mb", 1024L * 1024), ("kb", 1024L) })
        {
            if (bytes >= scale && bytes % scale == 0)
                return $"{bytes / scale}{suffix}";
        }
        return bytes.ToString();
    }


    /// <summary>A prefix is a route, so it needs a leading slash and no trailing one.</summary>
    public static string NormalizePrefix(string prefix)
    {
        var value = prefix.Trim();
        if (value.Length == 0 || value == "/")
            return "/";

        if (!value.StartsWith('/'))
            value = "/" + value;

        return value.TrimEnd('/');
    }
}

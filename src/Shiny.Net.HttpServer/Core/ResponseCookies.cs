using System.Globalization;
using System.Text;

namespace Shiny.Net.HttpServer;

/// <summary>Options for a Set-Cookie response header.</summary>
public sealed class CookieOptions
{
    public string? Domain { get; set; }
    public string? Path { get; set; } = "/";
    public DateTimeOffset? Expires { get; set; }
    public TimeSpan? MaxAge { get; set; }
    public bool Secure { get; set; }
    public bool HttpOnly { get; set; }
    public SameSiteMode SameSite { get; set; } = SameSiteMode.Unspecified;
}

public enum SameSiteMode
{
    Unspecified = -1,
    None = 0,
    Lax = 1,
    Strict = 2
}

/// <summary>Appends Set-Cookie headers to a response.</summary>
public sealed class ResponseCookies
{
    readonly HttpResponse response;

    internal ResponseCookies(HttpResponse response) => this.response = response;

    public void Append(string key, string value) => this.Append(key, value, new CookieOptions());

    public void Append(string key, string value, CookieOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(options);

        var sb = new StringBuilder(64);
        sb.Append(key).Append('=').Append(Uri.EscapeDataString(value));

        if (!string.IsNullOrEmpty(options.Path))
            sb.Append("; path=").Append(options.Path);

        if (!string.IsNullOrEmpty(options.Domain))
            sb.Append("; domain=").Append(options.Domain);

        if (options.Expires is { } expires)
            sb.Append("; expires=").Append(expires.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture));

        if (options.MaxAge is { } maxAge)
            sb.Append("; max-age=").Append(((long)maxAge.TotalSeconds).ToString(CultureInfo.InvariantCulture));

        if (options.Secure)
            sb.Append("; secure");

        if (options.HttpOnly)
            sb.Append("; httponly");

        switch (options.SameSite)
        {
            case SameSiteMode.None: sb.Append("; samesite=none"); break;
            case SameSiteMode.Lax: sb.Append("; samesite=lax"); break;
            case SameSiteMode.Strict: sb.Append("; samesite=strict"); break;
        }

        this.response.Headers.Append(HeaderNames.SetCookie, sb.ToString());
    }

    /// <summary>Expires a cookie on the client by setting it to a past date.</summary>
    public void Delete(string key) => this.Delete(key, new CookieOptions());

    public void Delete(string key, CookieOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Expires = DateTimeOffset.UnixEpoch;
        options.MaxAge = TimeSpan.Zero;
        this.Append(key, string.Empty, options);
    }
}

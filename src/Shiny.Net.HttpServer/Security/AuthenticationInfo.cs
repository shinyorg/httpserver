namespace Shiny.Net.HttpServer.Security;

/// <summary>
/// What happened when the server tried to identify the caller. Separate from
/// <see cref="HttpContext.User"/> because "nobody presented credentials" and "somebody presented
/// bad ones" are different situations that produce different responses.
/// </summary>
public sealed class AuthenticationInfo
{
    /// <summary>The scheme that handled (or rejected) the request, e.g. <c>Bearer</c>.</summary>
    public string? Scheme { get; internal set; }

    /// <summary>Why the credentials were rejected, when they were.</summary>
    public string? Failure { get; internal set; }

    /// <summary>True when credentials were supplied and did not check out.</summary>
    public bool Failed => this.Failure is not null;

    internal void Reset()
    {
        this.Scheme = null;
        this.Failure = null;
    }
}

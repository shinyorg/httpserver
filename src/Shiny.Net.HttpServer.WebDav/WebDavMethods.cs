namespace Shiny.Net.HttpServer.WebDav;

/// <summary>
/// The methods RFC 4918 adds on top of HTTP.
/// <para>
/// They are ordinary extension methods as far as this server is concerned — the request parser
/// accepts any token and the router keys endpoints on the string — so nothing in the core had to
/// learn about them.
/// </para>
/// </summary>
public static class WebDavMethods
{
    /// <summary>Reads properties from a resource and, by depth, its members.</summary>
    public const string PropFind = "PROPFIND";

    /// <summary>Sets and removes dead properties.</summary>
    public const string PropPatch = "PROPPATCH";

    /// <summary>Creates a collection.</summary>
    public const string MkCol = "MKCOL";

    /// <summary>Copies a resource, or a whole subtree, to the <c>Destination</c>.</summary>
    public const string Copy = "COPY";

    /// <summary>Moves a resource, or a whole subtree, to the <c>Destination</c>.</summary>
    public const string Move = "MOVE";

    /// <summary>Takes a write lock. Class 2 only.</summary>
    public const string Lock = "LOCK";

    /// <summary>Releases a write lock. Class 2 only.</summary>
    public const string Unlock = "UNLOCK";
}

/// <summary>The headers RFC 4918 adds, spelled as they go on the wire.</summary>
public static class WebDavHeaderNames
{
    /// <summary>Which compliance classes the resource supports — <c>1</c>, or <c>1, 2</c> with locking.</summary>
    public const string Dav = "DAV";

    /// <summary>How far into a collection an operation reaches: <c>0</c>, <c>1</c> or <c>infinity</c>.</summary>
    public const string Depth = "Depth";

    /// <summary>Where a <c>COPY</c> or <c>MOVE</c> is going.</summary>
    public const string Destination = "Destination";

    /// <summary>Lock tokens and entity tags the request is conditional on.</summary>
    public const string If = "If";

    /// <summary>The token an <c>UNLOCK</c> releases, or the one a <c>LOCK</c> refreshes.</summary>
    public const string LockToken = "Lock-Token";

    /// <summary><c>T</c> to replace an existing destination, <c>F</c> to fail instead.</summary>
    public const string Overwrite = "Overwrite";

    /// <summary>How long a lock is wanted for.</summary>
    public const string Timeout = "Timeout";

    /// <summary>
    /// Tells the Microsoft WebDAV redirector to author over DAV rather than FrontPage RPC. Without
    /// it, Windows Explorer probes for FrontPage first and mounts more slowly.
    /// </summary>
    public const string MsAuthorVia = "MS-Author-Via";
}

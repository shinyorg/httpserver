namespace Shiny.Net.HttpServer.CommandLine;


/// <summary>
/// Splits the mount's single write switch into create and update.
/// <para>
/// WebDAV has one permission for writing, so create and update are the same <c>PUT</c> to it -
/// and the same <c>MKCOL</c>, <c>COPY</c> and <c>MOVE</c>. Asking the file system whether the
/// target already exists is the only thing that tells them apart, and it has to happen before the
/// handler runs: after that the file is already written.
/// </para>
/// <para>
/// Deleting needs none of this. It has a permission of its own on the mount, so it never reaches
/// here.
/// </para>
/// </summary>
public sealed class WriteGuard(string prefix, string rootPath, Permissions permissions) : IHttpMiddleware
{
    // "/" already ends in the separator; anything else needs one before the relative path starts
    readonly string pathPrefix = prefix == "/" ? "/" : prefix + "/";
    readonly string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));

    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var required = this.RequiredPermission(context.Request);
        if (required != null && !permissions.Has(required.Value))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response
                .WriteTextAsync($"{required.Value.ToString().ToLowerInvariant()} is not allowed on this server")
                .ConfigureAwait(false);

            return;
        }
        await next(context).ConfigureAwait(false);
    }


    /// <summary>Null when this request is not a write, or when the path is one the mount will reject anyway.</summary>
    Permissions? RequiredPermission(HttpRequest request)
    {
        // PROPPATCH and LOCK are left out on purpose. Neither changes a file's contents, and both
        // are sent by Finder and by the Windows redirector around an ordinary upload - refusing
        // them on a create-only server would break the upload the server does allow.
        switch (request.Method.ToUpperInvariant())
        {
            case "PUT":
                return this.ForTarget(request.Path);

            // A collection either exists - in which case MKCOL is the mount's own 405 - or is about
            // to, which is a create however the server is configured.
            case "MKCOL":
                return Permissions.Create;

            // Both land bytes at the destination, and the destination is what decides which write
            // it is. MOVE also removes the source, which the mount's delete permission gates.
            case "COPY":
            case "MOVE":
                return request.Headers.GetFirst("Destination") is { Length: > 0 } destination
                    ? this.ForTarget(DestinationPath(destination))
                    : null;

            default:
                return null;
        }
    }


    /// <summary>What writing to this path would be: replacing something, or making it.</summary>
    Permissions? ForTarget(string path)
    {
        if (!path.StartsWith(this.pathPrefix, StringComparison.Ordinal))
            return null;

        var relative = path[this.pathPrefix.Length..];
        if (relative.Length == 0)
            return null;

        // a trailing slash names a collection, which is only ever created
        if (relative.EndsWith('/'))
            return Permissions.Create;

        var target = this.Resolve(relative);
        if (target == null)
            return null;

        return File.Exists(target) || Directory.Exists(target) ? Permissions.Update : Permissions.Create;
    }


    /// <summary>
    /// The path part of a <c>Destination</c>, which RFC 4918 allows to be a full URL. Percent
    /// decoded, because that is the form the request path arrives in and the two are compared.
    /// </summary>
    static string DestinationPath(string destination)
    {
        var value = destination.Trim();

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
            value = absolute.AbsolutePath;

        var query = value.IndexOfAny(['?', '#']);
        if (query >= 0)
            value = value[..query];

        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }


    /// <summary>The full path, or null when it escapes the root and the mount's own check will refuse it.</summary>
    string? Resolve(string relative)
    {
        try
        {
            var combined = Path.GetFullPath(
                Path.Combine(this.root, relative.Replace('/', Path.DirectorySeparatorChar))
            );
            var contained = combined.StartsWith(this.root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

            return contained ? combined : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

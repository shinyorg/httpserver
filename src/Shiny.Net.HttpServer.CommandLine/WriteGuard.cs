
namespace Shiny.Net.HttpServer.CommandLine;


/// <summary>
/// Splits the file browser's single PUT into create and update.
/// <para>
/// The browser has one write switch, so create and update are the same route to it. Asking the
/// file system whether the target already exists is the only thing that tells them apart, and it
/// has to happen before the handler runs - after that the file is already written.
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


    /// <summary>Null when this request is not a write, or when the path is one the browser will reject anyway.</summary>
    Permissions? RequiredPermission(HttpRequest request)
    {
        if (!String.Equals(request.Method, "PUT", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!request.Path.StartsWith(this.pathPrefix, StringComparison.Ordinal))
            return null;

        var relative = request.Path[this.pathPrefix.Length..];
        if (relative.Length == 0)
            return null;

        // a trailing slash is the browser's directory create, never an overwrite
        if (relative.EndsWith('/'))
            return Permissions.Create;

        var target = this.Resolve(relative);
        if (target == null)
            return null;

        return File.Exists(target) ? Permissions.Update : Permissions.Create;
    }


    /// <summary>The full path, or null when it escapes the root and the browser's own check will refuse it.</summary>
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

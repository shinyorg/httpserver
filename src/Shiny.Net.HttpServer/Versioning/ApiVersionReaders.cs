using Shiny.Net.HttpServer.Routing;

namespace Shiny.Net.HttpServer.Versioning;

/// <summary>
/// Finds the API version a request asked for.
/// <para>
/// A reader returns the raw text of every version it found, not a parsed version. Two readers
/// finding <c>1.0</c> and <c>1</c> agree, two finding <c>1.0</c> and <c>2.0</c> do not, and text
/// that is not a version at all is a different error again — deciding which is the selector's job,
/// done once, rather than each reader's.
/// </para>
/// <para>
/// Readers are called only for requests that reach a versioned route, never for the rest.
/// </para>
/// </summary>
public interface IApiVersionReader
{
    /// <summary>Every version value present on the request, as written. Empty when there are none.</summary>
    IReadOnlyList<string> Read(HttpRequest request);
}

/// <summary>Factory for the readers that come in the box.</summary>
public static class ApiVersionReader
{
    /// <summary>
    /// The reader used when none is configured: the <c>api-version</c> query parameter, then a
    /// <c>{version:apiVersion}</c> route segment — the same default Asp.Versioning ships.
    /// </summary>
    public static IApiVersionReader Default { get; } = Combine(
        new QueryStringApiVersionReader(),
        new UrlSegmentApiVersionReader()
    );

    /// <summary>
    /// Reads from several places at once. A request may say its version in more than one of them, as
    /// long as they agree; if they disagree the request is ambiguous and is answered 400.
    /// <code>
    /// o.ApiVersionReader = ApiVersionReader.Combine(
    ///     new UrlSegmentApiVersionReader(),
    ///     new QueryStringApiVersionReader("api-version"),
    ///     new HeaderApiVersionReader("api-version"),
    ///     new MediaTypeApiVersionReader("v"));
    /// </code>
    /// </summary>
    public static IApiVersionReader Combine(params IApiVersionReader[] readers)
    {
        ArgumentNullException.ThrowIfNull(readers);

        if (readers.Length == 0)
            throw new ArgumentException("Combine needs at least one reader.", nameof(readers));

        return readers.Length == 1 ? readers[0] : new CombinedApiVersionReader([.. readers]);
    }

    /// <inheritdoc cref="Combine(IApiVersionReader[])"/>
    public static IApiVersionReader Combine(IEnumerable<IApiVersionReader> readers)
    {
        ArgumentNullException.ThrowIfNull(readers);
        return Combine([.. readers]);
    }

    sealed class CombinedApiVersionReader(IApiVersionReader[] readers) : IApiVersionReader
    {
        public IReadOnlyList<string> Read(HttpRequest request)
        {
            List<string>? all = null;

            foreach (var reader in readers)
            {
                var found = reader.Read(request);
                if (found.Count == 0)
                    continue;

                all ??= [];
                all.AddRange(found);
            }

            return all ?? (IReadOnlyList<string>)[];
        }
    }
}

/// <summary>
/// Reads the version from a query parameter: <c>GET /users?api-version=2.0</c>.
/// </summary>
public sealed class QueryStringApiVersionReader : IApiVersionReader
{
    readonly string[] parameterNames;

    /// <summary>Reads <c>api-version</c>.</summary>
    public QueryStringApiVersionReader() : this("api-version")
    {
    }

    /// <summary>Reads each of the named query parameters.</summary>
    public QueryStringApiVersionReader(params string[] parameterNames)
    {
        ArgumentNullException.ThrowIfNull(parameterNames);

        if (parameterNames.Length == 0)
            throw new ArgumentException("Name at least one query parameter.", nameof(parameterNames));

        this.parameterNames = parameterNames;
    }

    public IReadOnlyList<string> ParameterNames => this.parameterNames;

    public IReadOnlyList<string> Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<string>? found = null;

        foreach (var name in this.parameterNames)
        {
            if (!request.Query.TryGetValue(name, out var values))
                continue;

            foreach (var value in values)
            {
                if (!String.IsNullOrWhiteSpace(value))
                    (found ??= []).Add(value);
            }
        }

        return found ?? (IReadOnlyList<string>)[];
    }
}

/// <summary>
/// Reads the version from a request header: <c>api-version: 2.0</c>. A comma-separated value is
/// several values, so a proxy that folded two headers together is seen as saying two things.
/// </summary>
public sealed class HeaderApiVersionReader : IApiVersionReader
{
    readonly string[] headerNames;

    /// <summary>Reads <c>api-version</c>.</summary>
    public HeaderApiVersionReader() : this("api-version")
    {
    }

    /// <summary>Reads each of the named headers.</summary>
    public HeaderApiVersionReader(params string[] headerNames)
    {
        ArgumentNullException.ThrowIfNull(headerNames);

        if (headerNames.Length == 0)
            throw new ArgumentException("Name at least one header.", nameof(headerNames));

        this.headerNames = headerNames;
    }

    public IReadOnlyList<string> HeaderNames => this.headerNames;

    public IReadOnlyList<string> Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<string>? found = null;

        foreach (var name in this.headerNames)
        {
            if (!request.Headers.TryGetValue(name, out var values))
                continue;

            foreach (var value in values)
            {
                if (value is null)
                    continue;

                foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    (found ??= []).Add(part);
            }
        }

        return found ?? (IReadOnlyList<string>)[];
    }
}

/// <summary>
/// Reads the version from a media type parameter: <c>Accept: application/json; v=2.0</c>, or the
/// same on <c>Content-Type</c> for a request with a body.
/// </summary>
public sealed class MediaTypeApiVersionReader : IApiVersionReader
{
    /// <summary>Reads the <c>v</c> parameter.</summary>
    public MediaTypeApiVersionReader() : this("v")
    {
    }

    /// <summary>Reads the named media type parameter.</summary>
    public MediaTypeApiVersionReader(string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        this.ParameterName = parameterName;
    }

    public string ParameterName { get; }

    public IReadOnlyList<string> Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<string>? found = null;

        this.ReadHeader(request.Headers, HeaderNames.ContentType, ref found);
        this.ReadHeader(request.Headers, HeaderNames.Accept, ref found);

        return found ?? (IReadOnlyList<string>)[];
    }

    void ReadHeader(HeaderDictionary headers, string name, ref List<string>? found)
    {
        if (!headers.TryGetValue(name, out var values))
            return;

        foreach (var value in values)
        {
            if (value is null)
                continue;

            // Each comma-separated entry is its own media range; each ';' after the type is one
            // parameter. Quoted values are unwrapped because v="2.0" is legal and occasionally sent.
            foreach (var range in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parameters = range.Split(';', StringSplitOptions.TrimEntries);

                for (var i = 1; i < parameters.Length; i++)
                {
                    var equals = parameters[i].IndexOf('=');
                    if (equals <= 0)
                        continue;

                    var key = parameters[i].AsSpan(0, equals).Trim();
                    if (!key.Equals(this.ParameterName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var parameterValue = parameters[i][(equals + 1)..].Trim().Trim('"');
                    if (parameterValue.Length > 0)
                        (found ??= []).Add(parameterValue);
                }
            }
        }
    }
}

/// <summary>
/// Reads the version from the route: the segment of the matched template carrying the
/// <c>apiVersion</c> constraint, as in <c>/api/{version:apiVersion}/users</c> answering
/// <c>/api/v2/users</c>.
/// <para>
/// The segment is found by its constraint rather than by a parameter name, so it can be called
/// whatever reads best in the template.
/// </para>
/// </summary>
public sealed class UrlSegmentApiVersionReader : IApiVersionReader
{
    public IReadOnlyList<string> Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.HttpContext.Endpoint is not RouteEndpoint endpoint)
            return [];

        foreach (var segment in endpoint.Template.Segments)
        {
            if (segment.Kind == RouteSegmentKind.Parameter &&
                segment.Constraint.IsApiVersion &&
                request.RouteValues.TryGetValue(segment.Text, out var value))
                return [value];
        }

        return [];
    }
}

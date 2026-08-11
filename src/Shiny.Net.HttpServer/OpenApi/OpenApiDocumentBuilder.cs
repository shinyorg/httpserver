using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Shiny.Net.HttpServer.Routing;

namespace Shiny.Net.HttpServer.OpenApi;

/// <summary>
/// Builds an OpenAPI 3.0.3 document from the route table.
/// <para>
/// Written with <see cref="Utf8JsonWriter"/> straight to bytes rather than serialized from an
/// object model. There is no document type to keep in sync with the spec, no serializer metadata to
/// register for it, and nothing for the trimmer to remove — the document generator is subject to
/// the same AOT rules as everything else here.
/// </para>
/// <para>
/// 3.0.3 rather than 3.1 deliberately: the two differ mostly in how they spell nullability, and
/// 3.0 is what the widest set of client generators and UIs still read.
/// </para>
/// </summary>
public static class OpenApiDocumentBuilder
{
    /// <summary>Builds the document as UTF-8 bytes, ready to write to a response or a file.</summary>
    public static byte[] Build(HttpServer server, OpenApiOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(server);

        options ??= new OpenApiOptions();

        var buffer = new ArrayBufferWriter<byte>(4096);
        var writerOptions = new JsonWriterOptions
        {
            Indented = options.Indented,

            // The default encoder escapes every non-ASCII character, which turns a summary
            // containing an em dash into — and makes the document unpleasant to read. This
            // encoder leaves Unicode alone but still escapes the characters that matter if the
            // document ends up inside an HTML page.
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };

        using (var writer = new Utf8JsonWriter(buffer, writerOptions))
            Write(writer, server.Router, options);

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Builds the document as a string. Handy for tests and for writing it out at build time.</summary>
    public static string BuildJson(HttpServer server, OpenApiOptions? options = null)
        => Encoding.UTF8.GetString(Build(server, options));

    static void Write(Utf8JsonWriter writer, Router router, OpenApiOptions options)
    {
        var schemas = new OpenApiSchemaWriter();
        var paths = GroupByPath(router, options);

        writer.WriteStartObject();
        writer.WriteString("openapi", "3.0.3");

        writer.WriteStartObject("info");
        writer.WriteString("title", options.Title);
        writer.WriteString("version", options.Version);
        if (options.Description is { Length: > 0 } description)
            writer.WriteString("description", description);
        writer.WriteEndObject();

        if (options.Servers.Count > 0)
        {
            writer.WriteStartArray("servers");
            foreach (var server in options.Servers)
            {
                writer.WriteStartObject();
                writer.WriteString("url", server);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        writer.WriteStartObject("paths");
        foreach (var (path, operations) in paths)
        {
            writer.WriteStartObject(path);
            foreach (var (method, operation) in operations)
                WriteOperation(writer, schemas, method, operation, options);

            writer.WriteEndObject();
        }
        writer.WriteEndObject();

        // Components last: writing the operations is what discovers which schemas are needed.
        if (schemas.HasComponents || options.SecuritySchemes.Count > 0)
        {
            writer.WriteStartObject("components");
            schemas.WriteSchemas(writer);
            WriteSecuritySchemes(writer, options);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.Flush();
    }

    /// <summary>
    /// Collapses the route table into OpenAPI's path-then-method shape, keeping paths in a stable
    /// order so the document does not churn between runs.
    /// </summary>
    static SortedDictionary<string, SortedDictionary<string, ApiOperation>> GroupByPath(
        Router router,
        OpenApiOptions options
    )
    {
        var paths = new SortedDictionary<string, SortedDictionary<string, ApiOperation>>(StringComparer.Ordinal);

        foreach (var endpoint in router.Endpoints)
        {
            var declared = endpoint.GetMetadata<ApiOperation>();
            if (declared is { Exclude: true })
                continue;

            if (declared is null && !options.IncludeUndescribedRoutes)
                continue;

            var operation = declared ?? new ApiOperation();
            EnsurePathParameters(operation, endpoint.Template);
            EnsureResponses(operation);

            // Read from the authorization metadata rather than asked for separately: an endpoint is
            // documented as protected because it is protected.
            if (endpoint.GetMetadata<Security.AuthorizationMetadata>() is { Required: true, AllowAnonymous: false })
                operation.RequiresAuthorization = true;

            options.ConfigureOperation?.Invoke(operation, endpoint);

            var method = endpoint.Method.ToLowerInvariant();

            foreach (var path in PathsFor(endpoint.Template))
            {
                if (!paths.TryGetValue(path.Path, out var operations))
                    paths[path.Path] = operations = new SortedDictionary<string, ApiOperation>(StringComparer.Ordinal);

                // A duplicate here means two endpoints collapsed onto one path+method, which the
                // router already refuses. First one wins rather than throwing at document time.
                operations.TryAdd(method, path.OmittedParameter is null
                    ? operation
                    : WithoutParameter(operation, path.OmittedParameter));
            }
        }

        return paths;
    }

    /// <summary>
    /// The OpenAPI paths one template covers. Normally one — but a trailing optional parameter
    /// matches two URLs and OpenAPI has no way to say "optional" about a path segment, so it
    /// becomes two paths.
    /// </summary>
    static List<(string Path, string? OmittedParameter)> PathsFor(RouteTemplate template)
    {
        var full = BuildPath(template, template.Segments.Count);
        var results = new List<(string, string?)> { (full, null) };

        if (template.Segments is [.., { IsOptional: true } last])
            results.Add((BuildPath(template, template.Segments.Count - 1), last.Text));

        return results;
    }

    static string BuildPath(RouteTemplate template, int segmentCount)
    {
        if (segmentCount == 0)
            return "/";

        var builder = new StringBuilder();
        for (var i = 0; i < segmentCount; i++)
        {
            var segment = template.Segments[i];
            builder.Append('/');

            // Constraints and the catch-all marker are routing syntax, not part of the URL.
            if (segment.Kind == RouteSegmentKind.Literal)
                builder.Append(segment.Text);
            else
                builder.Append('{').Append(segment.Text).Append('}');
        }

        return builder.ToString();
    }

    static ApiOperation WithoutParameter(ApiOperation operation, string name)
    {
        var copy = new ApiOperation
        {
            Summary = operation.Summary,
            Description = operation.Description,
            OperationId = operation.OperationId is { } id ? id + "_Short" : null,
            Deprecated = operation.Deprecated,
            RequiresAuthorization = operation.RequiresAuthorization,
            RequestBody = operation.RequestBody
        };

        foreach (var tag in operation.Tags)
            copy.Tags.Add(tag);

        foreach (var parameter in operation.Parameters)
        {
            if (!string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))
                copy.Parameters.Add(parameter);
        }

        foreach (var response in operation.Responses)
            copy.Responses.Add(response);

        return copy;
    }

    /// <summary>
    /// Fills in path parameters the template declares but nobody described — which is every raw
    /// route, and any generated one whose token binds somewhere unusual.
    /// </summary>
    static void EnsurePathParameters(ApiOperation operation, RouteTemplate template)
    {
        foreach (var segment in template.Segments)
        {
            if (segment.Kind == RouteSegmentKind.Literal)
                continue;

            var alreadyDescribed = operation.Parameters.Any(
                p => p.In == ApiParameterLocation.Path
                    && string.Equals(p.Name, segment.Text, StringComparison.OrdinalIgnoreCase)
            );

            if (alreadyDescribed)
                continue;

            operation.Parameters.Add(new ApiParameter
            {
                Name = segment.Text,
                In = ApiParameterLocation.Path,
                Type = TypeForConstraint(segment.Constraint),
                Required = true
            });
        }
    }

    static Type TypeForConstraint(RouteConstraint constraint) => constraint.ToString() switch
    {
        "int" => typeof(int),
        "long" => typeof(long),
        "guid" => typeof(Guid),
        "bool" => typeof(bool),
        "double" => typeof(double),
        "decimal" => typeof(decimal),
        _ => typeof(string)
    };

    static void EnsureResponses(ApiOperation operation)
    {
        if (operation.Responses.Count == 0)
            operation.Responses.Add(new ApiResponse { StatusCode = StatusCodes.Status200OK });
    }

    static void WriteSecuritySchemes(Utf8JsonWriter writer, OpenApiOptions options)
    {
        if (options.SecuritySchemes.Count == 0)
            return;

        writer.WriteStartObject("securitySchemes");

        foreach (var (name, scheme) in options.SecuritySchemes)
        {
            writer.WriteStartObject(name);
            writer.WriteString("type", scheme.Type);

            if (scheme.Scheme is { Length: > 0 } httpScheme)
                writer.WriteString("scheme", httpScheme);

            if (scheme.BearerFormat is { Length: > 0 } format)
                writer.WriteString("bearerFormat", format);

            if (scheme.In is { Length: > 0 } location)
                writer.WriteString("in", location);

            if (scheme.Name is { Length: > 0 } parameterName)
                writer.WriteString("name", parameterName);

            if (scheme.Description is { Length: > 0 } description)
                writer.WriteString("description", description);

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    static void WriteOperation(
        Utf8JsonWriter writer,
        OpenApiSchemaWriter schemas,
        string method,
        ApiOperation operation,
        OpenApiOptions options
    )
    {
        writer.WriteStartObject(method);

        if (operation.Tags.Count > 0)
        {
            writer.WriteStartArray("tags");
            foreach (var tag in operation.Tags)
                writer.WriteStringValue(tag);
            writer.WriteEndArray();
        }

        if (operation.Summary is { Length: > 0 } summary)
            writer.WriteString("summary", summary);

        if (operation.Description is { Length: > 0 } description)
            writer.WriteString("description", description);

        if (operation.OperationId is { Length: > 0 } operationId)
            writer.WriteString("operationId", operationId);

        if (operation.Deprecated)
            writer.WriteBoolean("deprecated", true);

        // An empty scope array is how a non-OAuth scheme says "this token, no scopes".
        if (operation.RequiresAuthorization && options.DefaultSecurityScheme is { Length: > 0 } scheme)
        {
            writer.WriteStartArray("security");
            writer.WriteStartObject();
            writer.WriteStartArray(scheme);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        if (operation.Parameters.Count > 0)
        {
            writer.WriteStartArray("parameters");
            foreach (var parameter in operation.Parameters)
            {
                writer.WriteStartObject();
                writer.WriteString("name", parameter.Name);
                writer.WriteString("in", parameter.In.ToString().ToLowerInvariant());

                // A path parameter is required by definition; the spec rejects anything else.
                writer.WriteBoolean("required", parameter.In == ApiParameterLocation.Path || parameter.Required);

                if (parameter.Description is { Length: > 0 } text)
                    writer.WriteString("description", text);

                writer.WritePropertyName("schema");
                schemas.WriteSchema(writer, parameter.Type);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        if (operation.RequestBody is { } body)
        {
            writer.WriteStartObject("requestBody");
            writer.WriteBoolean("required", body.Required);

            if (body.Description is { Length: > 0 } text)
                writer.WriteString("description", text);

            writer.WriteStartObject("content");
            writer.WriteStartObject(body.ContentType);
            writer.WritePropertyName("schema");
            schemas.WriteSchema(writer, body.Type);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteStartObject("responses");
        foreach (var response in operation.Responses.OrderBy(r => r.StatusCode))
        {
            writer.WriteStartObject(response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString(
                "description",
                response.Description ?? StatusCodes.GetReasonPhrase(response.StatusCode)
            );

            if (response.Type is { } payload)
            {
                writer.WriteStartObject("content");
                writer.WriteStartObject(response.ContentType);
                writer.WritePropertyName("schema");
                schemas.WriteSchema(writer, payload);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }
        writer.WriteEndObject();

        writer.WriteEndObject();
    }
}

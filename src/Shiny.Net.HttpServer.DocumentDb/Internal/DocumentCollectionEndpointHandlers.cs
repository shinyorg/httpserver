using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Shiny.DocumentDb;
using Shiny.Net.HttpServer.OpenApi;

namespace Shiny.Net.HttpServer.DocumentDb.Internal;

/// <summary>
/// The schema-free lane. There is no CLR type here, so the scope is a string-grammar clause rather than an
/// expression and it is enforced in SQL on every path — including the writes, which resolve their target
/// through the scoped query before touching it.
/// </summary>
static class DocumentCollectionEndpointHandlers
{
    /// <summary>Mapped as <see cref="RequestDelegate"/>s for the same AOT reason as the typed lane.</summary>
    public static List<RouteEndpointBuilder> Map(
        IEndpointRouteBuilder endpoints,
        string collectionName,
        DocumentCollectionEndpointOptions options
    )
    {
        var ops = options.Operations;
        var routes = new List<RouteEndpointBuilder>();

        if (ops.HasFlag(DocumentEndpoints.Read))
        {
            routes.Add(endpoints
                .Map(HttpMethods.Get, "/", Handler(http => List(http, collectionName, options)))
                .Describe(o => Describe(o, $"List-{collectionName}", collectionName, Json(200))));

            routes.Add(endpoints
                .Map(HttpMethods.Get, "/{id}", Handler(http => GetById(http, RouteId(http), collectionName, options)))
                .Describe(o => Describe(o, $"Get-{collectionName}", collectionName, Json(200), Problem(404))));
        }

        if (ops.HasFlag(DocumentEndpoints.Count))
        {
            routes.Add(endpoints
                .Map(HttpMethods.Get, "/count", Handler(http => Count(http, collectionName, options)))
                .Describe(o => Describe(o, $"Count-{collectionName}", collectionName, Json(200))));
        }

        if (ops.HasFlag(DocumentEndpoints.Write))
        {
            routes.Add(endpoints
                .Map(HttpMethods.Post, "/", Handler(http => Insert(http, collectionName, options)))
                .Describe(o => Describe(o, $"Create-{collectionName}", collectionName)));

            routes.Add(endpoints
                .Map(HttpMethods.Put, "/{id}", Handler(http => Write(http, RouteId(http), collectionName, options, patch: false)))
                .Describe(o => Describe(o, $"Replace-{collectionName}", collectionName, Problem(404))));

            routes.Add(endpoints
                .Map(HttpMethods.Patch, "/{id}", Handler(http => Write(http, RouteId(http), collectionName, options, patch: true)))
                .Describe(o => Describe(o, $"Patch-{collectionName}", collectionName, Problem(404))));
        }

        if (ops.HasFlag(DocumentEndpoints.Delete))
        {
            routes.Add(endpoints
                .Map(HttpMethods.Delete, "/{id}", Handler(http => Delete(http, RouteId(http), collectionName, options)))
                .Describe(o => Describe(o, $"Delete-{collectionName}", collectionName, Problem(404))));
        }

        return routes;
    }

    static RequestDelegate Handler(Func<HttpContext, Task<IResult>> handler)
        => async http =>
        {
            var result = await Guard(http, () => handler(http)).ConfigureAwait(false);
            await result.ExecuteAsync(http).ConfigureAwait(false);
        };

    static string RouteId(HttpContext http)
        => http.Request.RouteValues["id"]?.ToString()
        ?? throw new BadRequestException("An id is required.");

    static void Describe(ApiOperation operation, string operationId, string tag, params ApiResponse[] responses)
    {
        operation.OperationId = operationId;
        operation.Tags.Add(tag);

        foreach (var response in responses)
            operation.Responses.Add(response);
    }

    static ApiResponse Json(int status)
        => new() { StatusCode = status, Type = typeof(JsonObject), ContentType = "application/json" };

    static ApiResponse Problem(int status)
        => new() { StatusCode = status, Type = typeof(ProblemDetails), ContentType = "application/problem+json" };

    static IJsonDocumentCollection Collection(HttpContext http, string name, DocumentCollectionEndpointOptions options)
    {
        var store = options.StoreName is null
            ? http.RequestServices.GetRequiredService<IDocumentStore>()
            : http.RequestServices.GetRequiredKeyedService<IDocumentStore>(options.StoreName);

        return store.Collection(name, options.IdProperty);
    }

    static async Task<IResult> Guard(HttpContext http, Func<Task<IResult>> handler)
    {
        try
        {
            return await handler().ConfigureAwait(false);
        }
        catch (BadRequestException ex)
        {
            return Results.Problem(StatusCodes.Status400BadRequest, detail: ex.Message);
        }
        catch (NotSupportedException ex)
        {
            // JSON collections are relational-only; a document-native provider says so here rather than at boot,
            // because the store may be swapped by configuration.
            return Results.Problem(StatusCodes.Status501NotImplemented, detail: ex.Message);
        }
        catch (JsonException ex)
        {
            return Results.Problem(StatusCodes.Status400BadRequest, detail: $"Malformed JSON body: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(StatusCodes.Status400BadRequest, detail: ex.Message);
        }
    }

    static async ValueTask<IReadOnlyList<string>> Scope(HttpContext http, DocumentCollectionEndpointOptions options)
    {
        if (options.Scopes.Count == 0)
            return [];

        var context = new DocumentScopeContext(http);
        var clauses = new List<string>(options.Scopes.Count);

        foreach (var scope in options.Scopes)
        {
            var clause = await scope(context).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(clause))
                throw new InvalidOperationException(
                    $"A scope callback for collection '{context.Http.Request.Path}' returned an empty clause. "
                    + "Return DocumentScope.DenyAllFilter to deny everything — empty is not a scope."
                );

            clauses.Add(clause);
        }

        return clauses;
    }

    static async Task<IJsonDocumentQuery> Build(
        HttpContext http,
        string name,
        DocumentCollectionEndpointOptions options,
        ListRequest request
    )
    {
        var query = Collection(http, name, options).Query();

        foreach (var clause in await Scope(http, options).ConfigureAwait(false))
            query = query.Where(clause);

        if (request.Filter != null)
            query = query.Where(request.Filter);

        var sorted = false;
        foreach (var (path, direction) in request.SortKeys())
        {
            query = query.OrderBy(direction is null ? path : $"{path} {direction}");
            sorted = true;
        }

        if (!sorted && options.DefaultOrderBy != null)
            query = query.OrderBy(options.DefaultOrderBy);

        return query;
    }

    static ListRequest Request(HttpContext http, DocumentCollectionEndpointOptions options)
        => ListRequest.Parse(http.Request.Query, options.DefaultPageSize, options.MaxPageSize, options.AllowedFields);

    static async Task<IResult> List(HttpContext http, string name, DocumentCollectionEndpointOptions options)
    {
        var request = Request(http, options);
        var query = await Build(http, name, options, request).ConfigureAwait(false);
        var ct = http.RequestAborted;

        var cursorPaged = http.Request.Query.ContainsKey("cursor");

        if (request.Fields != null)
        {
            if (cursorPaged)
                throw new BadRequestException(
                    "'fields' and 'cursor' cannot be combined. Use skip/take with a sparse fieldset, or drop "
                    + "'fields' to page by cursor."
                );

            var rows = await query
                .Project(request.Fields)
                .Paginate(request.Skip, request.Take)
                .ToList(ct)
                .ConfigureAwait(false);

            return Content(new JsonArray(rows.Cast<JsonNode?>().ToArray()));
        }

        if (cursorPaged)
        {
            var page = await query.ToCursorPage(request.Cursor, request.Take, ct).ConfigureAwait(false);
            return Content(Envelope(page.Items, page.NextCursor));
        }

        var items = await query.Paginate(request.Skip, request.Take).ToList(ct).ConfigureAwait(false);

        return Content(new JsonArray(items.Cast<JsonNode?>().ToArray()));
    }

    static async Task<IResult> GetById(HttpContext http, string id, string name, DocumentCollectionEndpointOptions options)
    {
        // Resolved through the scoped query, so an out-of-scope id is indistinguishable from a missing one.
        var query = await Build(http, name, options, Request(http, options)).ConfigureAwait(false);

        var match = await query
            .Where(IdClause(options.IdProperty, id))
            .FirstOrDefault(http.RequestAborted)
            .ConfigureAwait(false);

        return match is null ? NotFound(id) : Content(match);
    }

    static async Task<IResult> Count(HttpContext http, string name, DocumentCollectionEndpointOptions options)
    {
        var query = await Build(http, name, options, Request(http, options)).ConfigureAwait(false);
        var count = await query.Count(http.RequestAborted).ConfigureAwait(false);

        return Content(new JsonObject { ["count"] = count });
    }

    static async Task<IResult> Insert(HttpContext http, string name, DocumentCollectionEndpointOptions options)
    {
        AssertUnscoped(options, "created");

        var body = await ReadObject(http).ConfigureAwait(false);
        var id = await Collection(http, name, options).Insert(body, http.RequestAborted).ConfigureAwait(false);

        return Results.Created($"{http.Request.Path.TrimEnd('/')}/{id}");
    }

    static async Task<IResult> Write(
        HttpContext http,
        string id,
        string name,
        DocumentCollectionEndpointOptions options,
        bool patch
    )
    {
        AssertUnscoped(options, patch ? "patched" : "replaced");

        var body = await ReadObject(http).ConfigureAwait(false);
        body[options.IdProperty] ??= id;

        var affected = await Collection(http, name, options)
            .Update(body, patch, http.RequestAborted)
            .ConfigureAwait(false);

        return affected == 0 ? NotFound(id) : Results.NoContent();
    }

    static async Task<IResult> Delete(HttpContext http, string id, string name, DocumentCollectionEndpointOptions options)
    {
        // Deletes resolve through the scoped query first, so this one IS safe alongside a scope.
        var query = await Build(http, name, options, Request(http, options)).ConfigureAwait(false);

        var match = await query
            .Where(IdClause(options.IdProperty, id))
            .FirstOrDefault(http.RequestAborted)
            .ConfigureAwait(false);

        if (match is null)
            return NotFound(id);

        await Collection(http, name, options).Remove(id, http.RequestAborted).ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>
    /// A scope and an insert/replace cannot be combined here, for the same reason the AI collection lane
    /// refuses it: a raw JSON body has no evaluator, so the boundary could not be enforced on the way in. The
    /// typed lane (<c>MapDocuments&lt;T&gt;</c>) enforces it on both sides of a write.
    /// </summary>
    static void AssertUnscoped(DocumentCollectionEndpointOptions options, string verb)
    {
        if (options.Scopes.Count > 0)
            throw new NotSupportedException(
                $"This collection endpoint has a Scope(...) and therefore cannot have documents {verb} through it — "
                + "a schema-free body cannot be checked against the scope before it is written. Map the type with "
                + "MapDocuments<T>() for scoped writes."
            );
    }

    static async Task<JsonObject> ReadObject(HttpContext http)
        => await JsonSerializer
            .DeserializeAsync(http.Request.Body, DocumentDbJson.Default.JsonObject, http.RequestAborted)
            .ConfigureAwait(false)
        ?? throw new BadRequestException("A request body must be a JSON object.");

    static IResult Content(JsonNode node) => Results.Content(node.ToJsonString(), "application/json");

    static IResult NotFound(string id)
        => Results.Problem(StatusCodes.Status404NotFound, detail: $"No document with id '{id}'.");

    static JsonObject Envelope(IReadOnlyList<JsonObject> items, string? nextCursor) => new()
    {
        ["items"] = new JsonArray(items.Cast<JsonNode?>().ToArray()),
        ["nextCursor"] = nextCursor
    };

    /// <summary>
    /// <c>id == 'value'</c> for the string grammar. The value is a quoted literal with its quotes doubled —
    /// the grammar's own escape — so a route id can never break out of the literal.
    /// </summary>
    static string IdClause(string idProperty, string id)
        => $"{idProperty} == '{id.Replace("'", "''")}'";
}

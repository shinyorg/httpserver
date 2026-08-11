using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Shiny.DocumentDb;
using Shiny.DocumentDb.Hosting;
using Shiny.DocumentDb.Internal;
using Shiny.Net.HttpServer.OpenApi;

namespace Shiny.Net.HttpServer.DocumentDb.Internal;

/// <summary>
/// The typed lane: one document type as a complete HTTP resource. Reads go through the raw-JSON terminals
/// where they can (a document read only to be written back out never becomes an object); everything that has
/// to enforce the scope in memory materializes, because that is what enforcement costs.
/// </summary>
static class DocumentEndpointHandlers<T> where T : class
{
    /// <summary>
    /// Every route is mapped as a <see cref="RequestDelegate"/> that reads the context itself, rather than a
    /// handler whose parameters something has to bind. That is what keeps this package AOT-clean: parameter
    /// binding by reflection is exactly the thing this server refuses to do, and the handlers already have the
    /// <see cref="HttpContext"/> in hand, so nothing is lost by it.
    /// </summary>
    public static List<RouteEndpointBuilder> Map(
        IEndpointRouteBuilder endpoints,
        DocumentEndpointOptions<T> options
    )
    {
        var ops = options.Operations;
        var name = typeof(T).Name;
        var routes = new List<RouteEndpointBuilder>();

        if (ops.HasFlag(DocumentEndpoints.Read))
        {
            routes.Add(endpoints
                .Map(HttpMethods.Get, "/", Handler(http => List(http, options)))
                .Describe(o => Describe(o, $"List{name}", name, Json(200))));

            routes.Add(endpoints
                .Map(HttpMethods.Get, "/{id}", Handler(http => GetById(http, RouteId(http), options)))
                .Describe(o => Describe(o, $"Get{name}", name, Json(200), Problem(404))));
        }

        if (ops.HasFlag(DocumentEndpoints.Count))
        {
            routes.Add(endpoints
                .Map(HttpMethods.Get, "/count", Handler(http => Count(http, options)))
                .Describe(o => Describe(o, $"Count{name}", name, Json(200))));
        }

        if (ops.HasFlag(DocumentEndpoints.Stream))
        {
            routes.Add(endpoints
                .Map(HttpMethods.Get, "/stream", http => Stream(http, options))
                .Describe(o => Describe(
                    o,
                    $"Stream{name}",
                    name,
                    new ApiResponse { StatusCode = 200, ContentType = "text/event-stream" }
                )));
        }

        if (ops.HasFlag(DocumentEndpoints.Write))
        {
            routes.Add(endpoints
                .Map(HttpMethods.Post, "/", Handler(http => Insert(http, options)))
                .Describe(o => Describe(o, $"Create{name}", name, Problem(400), Problem(409))));

            routes.Add(endpoints
                .Map(HttpMethods.Put, "/{id}", Handler(http => Replace(http, RouteId(http), options)))
                .Describe(o => Describe(o, $"Replace{name}", name, Problem(404), Problem(412))));

            routes.Add(endpoints
                .Map(HttpMethods.Patch, "/{id}", Handler(http => Patch(http, RouteId(http), options)))
                .Describe(o => Describe(o, $"Patch{name}", name, Problem(404), Problem(412))));
        }

        if (ops.HasFlag(DocumentEndpoints.Delete))
        {
            routes.Add(endpoints
                .Map(HttpMethods.Delete, "/{id}", Handler(http => Delete(http, RouteId(http), options)))
                .Describe(o => Describe(o, $"Delete{name}", name, Problem(404))));
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
        => new() { StatusCode = status, Type = typeof(T), ContentType = "application/json" };

    static ApiResponse Problem(int status)
        => new() { StatusCode = status, Type = typeof(ProblemDetails), ContentType = "application/problem+json" };

    // ── Plumbing ────────────────────────────────────────────────────────

    static IDocumentStore Store(HttpContext http, DocumentEndpointOptions<T> options)
        => options.StoreName is null
            ? http.RequestServices.GetRequiredService<IDocumentStore>()
            : http.RequestServices.GetRequiredKeyedService<IDocumentStore>(options.StoreName);

    /// <summary>
    /// One place that turns an exception into a response, so a malformed filter is a <c>400</c> rather than a
    /// stack trace and a provider gap is a <c>501</c> rather than a <c>500</c>.
    /// </summary>
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
        catch (PreconditionRequiredException ex)
        {
            return Results.Problem(StatusCodes.Status428PreconditionRequired, detail: ex.Message);
        }
        catch (ConcurrencyException ex)
        {
            return Results.Problem(StatusCodes.Status412PreconditionFailed, detail: ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return Results.Problem(StatusCodes.Status501NotImplemented, detail: ex.Message);
        }
        catch (JsonException ex)
        {
            return Results.Problem(StatusCodes.Status400BadRequest, detail: $"Malformed JSON body: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            // The grammar parser reports a bad filter this way. The caller's mistake, not the server's.
            return Results.Problem(StatusCodes.Status400BadRequest, detail: ex.Message);
        }
    }

    /// <summary>
    /// Resolves this request's scope: every registered callback, run inside the request's DI scope, AND-ed.
    /// A callback that throws fails the request — never "run without the predicate".
    /// </summary>
    static async ValueTask<ScopeSet> Scope(HttpContext http, DocumentEndpointOptions<T> options)
    {
        if (options.Scopes.Count == 0)
            return ScopeSet.Empty;

        var context = new DocumentScopeContext(http);
        var expressions = new List<Expression<Func<T, bool>>>(options.Scopes.Count);
        var predicates = new List<Func<T, bool>>(options.Scopes.Count);

        foreach (var scope in options.Scopes)
        {
            var expression = await scope(context).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"A scope callback for '{typeof(T).Name}' returned null. Return DocumentScope.DenyAll<{typeof(T).Name}>() "
                    + "to deny everything — null is not a scope."
                );

            expressions.Add(expression);

            // The same compile-free interpreter the AI tools use: one implementation, one set of semantics.
            // Compiling the expression instead would be a few lines shorter and would forfeit this package's
            // AOT guarantee on the one path that most needs to be right.
            predicates.Add(DocumentPredicate.Compile(expression));
        }

        return new ScopeSet(expressions, predicates.ToArray());
    }

    readonly struct ScopeSet(IReadOnlyList<Expression<Func<T, bool>>> expressions, Func<T, bool>[] predicates)
    {
        public static ScopeSet Empty { get; } = new([], []);

        public IDocumentQuery<T> ApplyTo(IDocumentQuery<T> query)
        {
            foreach (var expression in expressions)
                query = query.Where(expression);

            return query;
        }

        public bool Contains(T document)
        {
            foreach (var predicate in predicates)
            {
                if (!predicate(document))
                    return false;
            }

            return true;
        }
    }

    static JsonTypeInfo<T>? TypeInfo(IDocumentStore store, DocumentEndpointOptions<T> options)
        => options.TypeInfo ?? store.Query<T>().QueryTypeInfo;

    static VersionMapping? Version(IDocumentStore store, HttpContext http)
    {
        var mappings = store.GetMappings()
            ?? http.RequestServices.GetService<DocumentStoreOptions>()?.Mappings;

        return mappings?.ResolveVersionMapping(typeof(T));
    }

    static IDocumentQuery<T> Build(
        IDocumentStore store,
        DocumentEndpointOptions<T> options,
        ScopeSet scope,
        ListRequest request
    )
    {
        var typeInfo = TypeInfo(store, options);
        var query = scope.ApplyTo(store.Query(typeInfo));

        if (request.Filter != null)
            query = query.Where(request.Filter, typeInfo);

        var sorted = false;
        foreach (var (path, direction) in request.SortKeys())
        {
            query = query.OrderBy(path, direction, typeInfo);
            sorted = true;
        }

        if (!sorted && options.DefaultOrderBy != null)
            query = query.OrderBy(options.DefaultOrderBy);

        return query;
    }

    static JsonObject Envelope(IReadOnlyList<JsonObject> items, string? nextCursor) => new()
    {
        ["items"] = new JsonArray(items.Cast<JsonNode?>().ToArray()),
        ["nextCursor"] = nextCursor
    };

    /// <summary>Serializes a materialized document without re-encrypting what the store just decrypted.</summary>
    static string Serialize(T document, JsonTypeInfo<T>? typeInfo)
        => JsonSerializer.Serialize(document, DocumentEncryption.PlaintextView(Require(typeInfo)));

    /// <summary>
    /// The metadata, or a message saying how to supply it.
    /// <para>
    /// The ASP.NET build of these endpoints falls back to the reflection serializer here. This one cannot:
    /// the analyzers are on for every shipping project in this repo, and a fallback that works on a desktop
    /// and throws on a trimmed phone is worse than a clear error in both places.
    /// </para>
    /// </summary>
    static JsonTypeInfo<T> Require(JsonTypeInfo<T>? typeInfo)
        => typeInfo ?? throw new InvalidOperationException(
            $"No JSON metadata for '{typeof(T).Name}'. Set DocumentEndpointOptions<{typeof(T).Name}>.TypeInfo to "
            + $"the source-generated metadata (e.g. AppJson.Default.{typeof(T).Name}), or register a store that "
            + "supplies it. There is no reflection fallback here — this server is trim- and AOT-clean."
        );

    static IResult Content(JsonNode node) => Results.Content(node.ToJsonString(), "application/json");

    // ── Reads ───────────────────────────────────────────────────────────

    static async Task<IResult> List(HttpContext http, DocumentEndpointOptions<T> options)
    {
        var store = Store(http, options);
        var typeInfo = TypeInfo(store, options);
        var request = ListRequest.Parse(http.Request.Query, options.DefaultPageSize, options.MaxPageSize, options.AllowedFields);
        var scope = await Scope(http, options).ConfigureAwait(false);
        var query = Build(store, options, scope, request);
        var ct = http.RequestAborted;

        var cursorPaged = http.Request.Query.ContainsKey("cursor");

        // Sparse fieldset: Project already hands back JsonObjects, so there is nothing to save by taking the
        // raw lane — but paging still applies, on the projected query.
        if (request.Fields != null)
        {
            // Cursor paging is built from the sort/key columns, which a projection may not carry — the engine
            // refuses the combination. Say so plainly rather than surfacing a 501 from underneath.
            if (cursorPaged)
                throw new BadRequestException(
                    "'fields' and 'cursor' cannot be combined. Use skip/take with a sparse fieldset, or drop "
                    + "'fields' to page by cursor."
                );

            var rows = await query
                .Project(request.Fields, typeInfo)
                .Paginate(request.Skip, request.Take)
                .ToList(ct)
                .ConfigureAwait(false);

            return Content(new JsonArray(rows.Cast<JsonNode?>().ToArray()));
        }

        if (cursorPaged)
        {
            var page = await query.ToJsonCursorPage(request.Cursor, request.Take, ct).ConfigureAwait(false);
            return Content(Envelope(page.Items, page.NextCursor));
        }

        query = query.Paginate(request.Skip, request.Take);

        // The raw lane: the stored bodies stream straight to the socket, never buffered, never re-serialized.
        // SupportsRawJson is false for an encrypted type — only the typed path decrypts — so it is probed,
        // not assumed, and the typed fallback keeps the endpoint working rather than turning into a 501.
        if (query.SupportsRawJson)
            return new RawJsonArrayResult(query);

        var documents = await query.ToList(ct).ConfigureAwait(false);
        var json = new JsonArray(documents.Select(d => JsonNode.Parse(Serialize(d, typeInfo))).ToArray());

        return Content(json);
    }

    /// <summary>
    /// Writes the store's own JSON straight to the response body.
    /// <para>
    /// A result rather than a handler that writes and returns nothing, so the raw lane composes with
    /// <see cref="Guard"/> the same way every other path does — an exception raised while building the query
    /// still becomes a problem response rather than a half-written array.
    /// </para>
    /// </summary>
    sealed class RawJsonArrayResult(IDocumentQuery<T> query) : IResult
    {
        public async ValueTask ExecuteAsync(HttpContext context)
        {
            context.Response.ContentType = "application/json";

            await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
            await query.WriteJsonArrayTo(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
        }
    }

    static async Task<IResult> GetById(HttpContext http, string id, DocumentEndpointOptions<T> options)
    {
        var store = Store(http, options);
        var typeInfo = TypeInfo(store, options);
        var scope = await Scope(http, options).ConfigureAwait(false);

        // By-id materializes on purpose: the scope is evaluated in memory here, so there is nothing to save
        // by reading the raw body — and the version for the ETag comes off the same instance.
        var document = await store.Get(id, typeInfo, http.RequestAborted).ConfigureAwait(false);
        if (document is null || !scope.Contains(document))
            return NotFound(id);

        SetETag(http, store, document);

        return Results.Content(Serialize(document, typeInfo), "application/json");
    }

    static async Task<IResult> Count(HttpContext http, DocumentEndpointOptions<T> options)
    {
        var store = Store(http, options);
        var request = ListRequest.Parse(http.Request.Query, options.DefaultPageSize, options.MaxPageSize, options.AllowedFields);
        var scope = await Scope(http, options).ConfigureAwait(false);
        var query = Build(store, options, scope, request);

        var count = await query.Count(http.RequestAborted).ConfigureAwait(false);

        return Content(new JsonObject { ["count"] = count });
    }

    // ── Writes ──────────────────────────────────────────────────────────

    static async Task<IResult> Insert(HttpContext http, DocumentEndpointOptions<T> options)
    {
        var store = Store(http, options);
        var typeInfo = TypeInfo(store, options);
        var scope = await Scope(http, options).ConfigureAwait(false);

        var document = await ReadBody(http, typeInfo).ConfigureAwait(false);
        if (!scope.Contains(document))
            return Results.Problem(
                StatusCodes.Status400BadRequest,
                detail: "The document falls outside the scope this endpoint enforces and was not created."
            );

        try
        {
            await store.Insert(document, typeInfo, http.RequestAborted).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(StatusCodes.Status409Conflict, detail: ex.Message);
        }

        SetETag(http, store, document);

        var id = DocumentId(store, options, document);
        var path = http.Request.Path.TrimEnd('/');

        return id is null ? Results.StatusCode(StatusCodes.Status201Created) : Results.Created($"{path}/{id}");
    }

    static async Task<IResult> Replace(HttpContext http, string id, DocumentEndpointOptions<T> options)
    {
        var store = Store(http, options);
        var typeInfo = TypeInfo(store, options);
        var scope = await Scope(http, options).ConfigureAwait(false);

        var ifMatch = IfMatch(http, options);
        var incoming = await ReadBody(http, typeInfo).ConfigureAwait(false);

        if (!scope.Contains(incoming))
            return Results.Problem(
                StatusCodes.Status400BadRequest,
                detail: "The document falls outside the scope this endpoint enforces and was not saved."
            );

        var existing = await store.Get(id, typeInfo, http.RequestAborted).ConfigureAwait(false);
        if (existing is null || !scope.Contains(existing))
            return NotFound(id);

        var version = Version(store, http);
        if (ifMatch != null && version != null && version.GetVersion(existing) != ifMatch)
            return Precondition(version.GetVersion(existing));

        // Hand the store a version so its CAS has something to compare: the one the caller says it saw when
        // If-Match was supplied (a race between this read and the write is then the store's to catch), or the
        // current one when it was not — which is the documented last-writer-wins behavior of an optional
        // If-Match, rather than a 412 for every client that does not speak ETags.
        if (version != null)
            version.SetVersion(incoming, ifMatch ?? version.GetVersion(existing));

        await store.Update(incoming, typeInfo, http.RequestAborted).ConfigureAwait(false);

        var saved = await store.Get(id, typeInfo, http.RequestAborted).ConfigureAwait(false);
        if (saved != null)
            SetETag(http, store, saved);

        return Results.NoContent();
    }

    static async Task<IResult> Patch(HttpContext http, string id, DocumentEndpointOptions<T> options)
    {
        var store = Store(http, options);
        var typeInfo = TypeInfo(store, options);
        var scope = await Scope(http, options).ConfigureAwait(false);
        var ifMatch = IfMatch(http, options);

        var existing = await store.Get(id, typeInfo, http.RequestAborted).ConfigureAwait(false);
        if (existing is null || !scope.Contains(existing))
            return NotFound(id);

        var version = Version(store, http);
        if (ifMatch != null && version != null && version.GetVersion(existing) != ifMatch)
            return Precondition(version.GetVersion(existing));

        var patch = await JsonSerializer
            .DeserializeAsync(http.Request.Body, DocumentDbJson.Default.JsonObject, http.RequestAborted)
            .ConfigureAwait(false)
            ?? throw new BadRequestException("A PATCH body must be a JSON object.");

        // RFC 7396 is applied here, against the document we already read for the scope check, and the result is
        // written as a full replace. The store's own merge deliberately treats a null as "leave alone" — it has
        // to, because a serialized T carries nulls for every unset member — but over HTTP an explicit null is
        // the caller's word, and it means remove. Doing the merge here keeps that promise on every provider.
        var merged = JsonNode.Parse(Serialize(existing, typeInfo))!.AsObject();
        MergePatch.Apply(merged, patch);

        var collection = store.Collection(typeof(T));
        merged[collection.IdProperty] ??= id;

        await collection.Update(merged, patch: false, http.RequestAborted).ConfigureAwait(false);

        var saved = await store.Get(id, typeInfo, http.RequestAborted).ConfigureAwait(false);
        if (saved is null || !scope.Contains(saved))
            return Results.Problem(
                StatusCodes.Status400BadRequest,
                detail: "The patch would move the document outside the scope this endpoint enforces."
            );

        SetETag(http, store, saved);

        return Results.NoContent();
    }

    static async Task<IResult> Delete(HttpContext http, string id, DocumentEndpointOptions<T> options)
    {
        var store = Store(http, options);
        var typeInfo = TypeInfo(store, options);
        var scope = await Scope(http, options).ConfigureAwait(false);
        var ifMatch = IfMatch(http, options);

        var existing = await store.Get(id, typeInfo, http.RequestAborted).ConfigureAwait(false);
        if (existing is null || !scope.Contains(existing))
            return NotFound(id);

        var version = Version(store, http);
        if (ifMatch != null && version != null && version.GetVersion(existing) != ifMatch)
            return Precondition(version.GetVersion(existing));

        await store.Remove<T>(id, http.RequestAborted).ConfigureAwait(false);

        return Results.NoContent();
    }

    // ── SSE ─────────────────────────────────────────────────────────────

    static async ValueTask Stream(HttpContext http, DocumentEndpointOptions<T> options)
    {
        var store = Store(http, options);
        var typeInfo = TypeInfo(store, options);

        ScopeSet scope;
        Func<T, bool>? filter = null;

        try
        {
            var request = ListRequest.Parse(http.Request.Query, options.DefaultPageSize, options.MaxPageSize, options.AllowedFields);

            // The scope is evaluated ONCE, when the connection opens. A stream can outlive any sane notion of
            // "current permissions", so the app closes the connection when it needs re-authorization; the
            // values are captured here, never the services they came from.
            scope = await Scope(http, options).ConfigureAwait(false);

            if (request.Filter != null && typeInfo != null)
            {
                var predicate = DocumentFilter.Parse(request.Filter, typeInfo);
                filter = DocumentPredicate.Compile(predicate);
            }
        }
        catch (Exception ex) when (ex is BadRequestException or ArgumentException)
        {
            // Still before any bytes went out, so this can be an ordinary problem response.
            await Results
                .Problem(StatusCodes.Status400BadRequest, detail: ex.Message)
                .ExecuteAsync(http)
                .ConfigureAwait(false);

            return;
        }

        await Results
            .ServerSentEvents(stream => Pump(stream, store, scope, filter, typeInfo, options.StreamHeartbeat))
            .ExecuteAsync(http)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Pumps change events out, with a keep-alive whenever the store goes quiet.
    /// <para>
    /// One loop races the next change against the heartbeat interval, rather than a timer writing to the
    /// response from another thread — two things writing to one response is a data race that shows up as a
    /// corrupted event stream under load, and only under load.
    /// </para>
    /// </summary>
    static async Task Pump(
        Sse.ServerSentEventStream stream,
        IDocumentStore store,
        ScopeSet scope,
        Func<T, bool>? filter,
        JsonTypeInfo<T>? typeInfo,
        TimeSpan heartbeat
    )
    {
        var token = stream.Aborted;

        try
        {
            await using var changes = store.NotifyOnChange<T>(token).GetAsyncEnumerator(token);

            while (true)
            {
                var next = changes.MoveNextAsync().AsTask();
                var completed = await Task.WhenAny(next, Task.Delay(heartbeat, token)).ConfigureAwait(false);

                if (completed != next)
                {
                    await stream.SendHeartbeatAsync(token).ConfigureAwait(false);
                    continue;
                }

                if (!await next.ConfigureAwait(false))
                    break;

                var change = changes.Current;
                var document = change.Document;

                // A delete carries no document, so neither the scope nor the filter can be evaluated against
                // it; those events are dropped rather than leaked past a scope that cannot be checked.
                if (document is null || !scope.Contains(document) || (filter != null && !filter(document)))
                    continue;

                var name = change.ChangeType switch
                {
                    DocumentChangeType.Inserted => "insert",
                    DocumentChangeType.Updated => "update",
                    DocumentChangeType.Removed => "delete",
                    _ => "clear"
                };

                var payload = new JsonObject
                {
                    ["id"] = change.Id,
                    ["document"] = JsonNode.Parse(Serialize(document, typeInfo))
                };

                await stream.SendAsync(name, payload.ToJsonString(), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The client went away. The enumeration's disposal releases the store subscription.
        }
    }

    // ── ETag / concurrency ──────────────────────────────────────────────

    static void SetETag(HttpContext http, IDocumentStore store, T document)
    {
        var version = Version(store, http);
        if (version != null)
            http.Response.Headers["ETag"] = $"\"{version.GetVersion(document)}\"";
    }

    static int? IfMatch(HttpContext http, DocumentEndpointOptions<T> options)
    {
        var header = http.Request.Headers.GetFirst("If-Match");
        if (string.IsNullOrWhiteSpace(header))
        {
            if (options.RequireIfMatch)
                throw new PreconditionRequiredException();

            return null;
        }

        var trimmed = header.Trim().Trim('"');
        if (trimmed == "*")
            return null;

        return int.TryParse(trimmed, out var version)
            ? version
            : throw new BadRequestException(
                $"If-Match must be the document's version, e.g. If-Match: \"3\" (got '{header}')."
            );
    }

    static IResult NotFound(string id)
        => Results.Problem(StatusCodes.Status404NotFound, detail: $"No {typeof(T).Name} with id '{id}'.");

    static IResult Precondition(int current)
        => Results.Problem(
            StatusCodes.Status412PreconditionFailed,
            detail: $"The document has changed since the version you supplied (current version {current}). Re-read it and retry."
        );

    static async Task<T> ReadBody(HttpContext http, JsonTypeInfo<T>? typeInfo)
    {
        var document = await JsonSerializer
            .DeserializeAsync(http.Request.Body, Require(typeInfo), http.RequestAborted)
            .ConfigureAwait(false);

        return document ?? throw new BadRequestException("A request body is required.");
    }

    // The Location header needs whatever id the store just assigned — read back through the same accessor the
    // store uses, so a mapped/renamed id property is honored rather than guessed at.
    static string? DocumentId(IDocumentStore store, DocumentEndpointOptions<T> options, T document)
    {
        try
        {
            return IdAccessor<T>.Create(TypeInfo(store, options)).GetIdAsString(document);
        }
        catch
        {
            return null;
        }
    }
}

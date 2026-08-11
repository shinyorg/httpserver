using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer;

namespace Sample.Api;

// ---------------------------------------------------------------------------
// Tier 3: typed endpoints. Constructor-injected, strongly typed parameters, and
// results that are plain return values — so every method here is callable from a
// unit test with no HTTP involved at all.
//
// The generator turns this class into route registrations and binders at compile
// time. Nothing below is discovered at runtime, which is why it survives
// PublishAot untouched.
// ---------------------------------------------------------------------------

[Route("/api/widgets")]
public class WidgetEndpoints(IWidgetStore store, ILogger<WidgetEndpoints> logger)
{
    /// <summary>Fetches a single widget by id.</summary>
    [Get("/{id:int}")]
    [Produces(200, typeof(Widget))]
    [Produces(404, Description = "No widget has that id")]
    public async Task<IActionResult> GetWidget(int id, CancellationToken cancellationToken)
    {
        logger.LogInformation("Looking up widget {Id}", id);

        var widget = await store.FindAsync(id, cancellationToken);

        return widget is null
            ? new NotFoundResult()
            : new OkObjectResult(widget);
    }

    /// <summary>Lists widgets, optionally filtered by name.</summary>
    // No attributes needed: neither name is a route token, and both types can be parsed from a
    // string, so they bind from the query. The defaults make them optional.
    [Get]
    public async Task<IReadOnlyList<Widget>> ListWidgets(
        int take = 10,
        string? search = null,
        CancellationToken cancellationToken = default
    ) => await store.ListAsync(take, search, cancellationToken);

    /// <summary>Creates a widget.</summary>
    // A complex type on a verb that carries a body binds from JSON, again without an attribute.
    // A malformed body is a 400 before this method is ever called.
    [Post]
    [Produces(201, typeof(Widget))]
    [Produces(400, typeof(Problem), Description = "The request was not valid")]
    public async Task<IActionResult> CreateWidget(CreateWidget request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return new BadRequestObjectResult(new Problem("Name is required."));

        var created = await store.AddAsync(request.Name, cancellationToken);

        return new CreatedResult($"/api/widgets/{created.Id}", created);
    }

    /// <summary>Deletes a widget.</summary>
    [Delete("/{id:int}")]
    [Produces(204)]
    [Produces(404)]
    public async Task<IActionResult> DeleteWidget(int id, CancellationToken cancellationToken)
        => await store.RemoveAsync(id, cancellationToken)
            ? new NoContentResult()
            : new NotFoundResult();

    /// <summary>Searches widgets by an arbitrary multi-segment path.</summary>
    // A catch-all token binds as a string, and the explicit [FromHeader] shows the escape hatch
    // for when the convention is not what you want.
    [Get("/search/{*query}")]
    public string Search(string query, [FromHeader(Name = "User-Agent")] string? userAgent = null)
        => $"searching for '{query}' on behalf of {userAgent ?? "an anonymous client"}";
}

public record Widget(int Id, string Name);

public record CreateWidget(string Name);

public record Problem(string Message);

/// <summary>Used by the tier-1 routes in Program.cs, which pass JsonTypeInfo directly.</summary>
public record User(int Id, string Name);

public interface IWidgetStore
{
    ValueTask<Widget?> FindAsync(int id, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<Widget>> ListAsync(int take, string? search, CancellationToken cancellationToken);
    ValueTask<Widget> AddAsync(string name, CancellationToken cancellationToken);
    ValueTask<bool> RemoveAsync(int id, CancellationToken cancellationToken);
}

/// <summary>
/// One context listing everything that crosses an endpoint boundary as JSON. The generator emits a
/// module initializer registering it, so <c>new OkObjectResult(widget)</c> serializes from
/// compile-time metadata — and warns at build time (SWS006) about any type you forget.
/// </summary>
/// <remarks>
/// The context's own options decide the shape of the JSON, and both spellings of a JSON response —
/// <c>new OkObjectResult(widget)</c> and <c>Results.Ok(widget, SampleJson.Default.Widget)</c> —
/// go through this context, so they cannot disagree about casing.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Widget))]
[JsonSerializable(typeof(IReadOnlyList<Widget>))]
[JsonSerializable(typeof(CreateWidget))]
[JsonSerializable(typeof(Problem))]
[JsonSerializable(typeof(User))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(Identity))]
public partial class SampleJson : JsonSerializerContext;

namespace Shiny.Net.HttpServer;

/// <summary>
/// Declares a response an endpoint can produce.
/// <para>
/// The generator infers a 200 from the return type when it can see one, but a method returning
/// <c>IActionResult</c> deliberately hides its status codes — that is the point of the abstraction.
/// This is how you tell the document what they are.
/// </para>
/// <code>
/// [Get("/{id:int}")]
/// [Produces(200, typeof(Widget))]
/// [Produces(404)]
/// public IActionResult GetWidget(int id) => ...;
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class ProducesAttribute(int statusCode, Type? type = null) : Attribute
{
    public int StatusCode { get; } = statusCode;

    /// <summary>The response body type, or null for a response with no body.</summary>
    public Type? Type { get; } = type;

    public string? Description { get; set; }

    public string ContentType { get; set; } = "application/json";
}

/// <summary>
/// Groups operations in the document. Applied to a class it covers every endpoint on it; applied to
/// a method it replaces the class's tags for that one. Without it, the class name is the tag.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ApiTagsAttribute(params string[] tags) : Attribute
{
    public string[] Tags { get; } = tags;
}

/// <summary>
/// Omits a class or a single method from the OpenAPI document. The route still works — this is
/// about what is published, not what is served.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ApiExcludeAttribute : Attribute;

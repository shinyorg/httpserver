namespace Shiny.Net.HttpServer;

/// <summary>
/// Marks a class as an endpoint container — a "controller", in the familiar vocabulary. The source
/// generator finds these, emits a registration extension method, and binds every annotated method
/// on the class without a scrap of reflection.
/// <code>
/// [Route("/api/users")]
/// public class UserEndpoints(IUserService users)
/// {
///     [Get("/{id:int}")]
///     public async Task&lt;IActionResult&gt; GetUser(int id) => ...;
/// }
/// </code>
/// <para>
/// The class does not need to be partial and does not need to derive from anything. Constructor
/// parameters are resolved from the request scope, so a scoped dependency is the same instance the
/// rest of the request sees.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class RouteAttribute(string prefix = "") : Attribute
{
    /// <summary>Path prepended to every method template on this class. May be empty.</summary>
    public string Prefix { get; } = prefix;
}

/// <summary>
/// Base type for the verb attributes. Present so the generator has one thing to look for and so
/// unusual verbs can be mapped without a dedicated attribute per method name.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public class HttpMethodAttribute(string method, string template = "") : Attribute
{
    /// <summary>The HTTP method this endpoint answers.</summary>
    public string Method { get; } = method;

    /// <summary>Route template, appended to the class prefix. Empty means the prefix itself.</summary>
    public string Template { get; } = template;
}

/// <summary>Maps a method to <c>GET</c> at <paramref name="template"/>, relative to the class prefix.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class GetAttribute(string template = "") : HttpMethodAttribute(HttpMethods.Get, template);

/// <summary>Maps a method to <c>POST</c> at <paramref name="template"/>, relative to the class prefix.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class PostAttribute(string template = "") : HttpMethodAttribute(HttpMethods.Post, template);

/// <summary>Maps a method to <c>PUT</c> at <paramref name="template"/>, relative to the class prefix.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class PutAttribute(string template = "") : HttpMethodAttribute(HttpMethods.Put, template);

/// <summary>Maps a method to <c>DELETE</c> at <paramref name="template"/>, relative to the class prefix.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class DeleteAttribute(string template = "") : HttpMethodAttribute(HttpMethods.Delete, template);

/// <summary>Maps a method to <c>PATCH</c> at <paramref name="template"/>, relative to the class prefix.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class PatchAttribute(string template = "") : HttpMethodAttribute(HttpMethods.Patch, template);

/// <summary>Excludes a public method on an endpoint class from route discovery.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class NonEndpointAttribute : Attribute;

// ---- Parameter sources ----
//
// Binding works without any of these: a parameter whose name matches a route token binds from the
// route, a simple type binds from the query, a complex type binds from the JSON body, and anything
// registered in the container binds from services. The attributes exist for the cases where the
// convention guesses wrong, or where being explicit is simply clearer.

/// <summary>Binds the parameter from a route token.</summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class FromRouteAttribute : Attribute
{
    /// <summary>Route token name. Defaults to the parameter name.</summary>
    public string? Name { get; set; }
}

/// <summary>Binds the parameter from the query string.</summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class FromQueryAttribute : Attribute
{
    /// <summary>Query key. Defaults to the parameter name.</summary>
    public string? Name { get; set; }
}

/// <summary>Binds the parameter from a request header.</summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class FromHeaderAttribute : Attribute
{
    /// <summary>Header name. Defaults to the parameter name.</summary>
    public string? Name { get; set; }
}

/// <summary>
/// Binds the parameter by deserializing the JSON request body. At most one parameter per method.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class FromBodyAttribute : Attribute;

/// <summary>Resolves the parameter from the request's service scope.</summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class FromServicesAttribute : Attribute;

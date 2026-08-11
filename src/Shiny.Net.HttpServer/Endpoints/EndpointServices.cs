namespace Shiny.Net.HttpServer.Endpoints;

/// <summary>
/// Service resolution for generated endpoints. Thin, but it exists so a missing registration
/// produces a sentence naming the endpoint, the parameter and the type, rather than the generic
/// "no service for type X" that leaves you grepping for the call site.
/// </summary>
public static class EndpointServices
{
    /// <summary>Resolves a dependency, or explains precisely what is missing and where.</summary>
    public static T GetRequired<T>(IServiceProvider services, string endpoint, string parameter) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.GetService(typeof(T)) is T resolved)
            return resolved;

        throw new InvalidOperationException(
            $"Endpoint '{endpoint}' needs '{typeof(T).FullName}' for '{parameter}', but nothing is registered for it. " +
            (ReferenceEquals(services, EmptyServiceProvider.Instance)
                ? "This server was created without a service provider — use HttpServer.CreateBuilder() or pass one to the constructor."
                : "Register it on the container the server was built with.")
        );
    }

    /// <summary>
    /// Resolves the endpoint class itself when the app registered it, returning null when it did
    /// not — in which case the generated factory constructs it directly.
    /// </summary>
    public static T? Get<T>(IServiceProvider services) where T : class
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetService(typeof(T)) as T;
    }
}

/// <summary>
/// Metadata the generator attaches to every endpoint it registers, so logs and diagnostics can name
/// the class and method behind a route instead of just its template.
/// </summary>
public sealed class EndpointDescriptor(string className, string methodName)
{
    public string ClassName { get; } = className;

    public string MethodName { get; } = methodName;

    public override string ToString() => $"{this.ClassName}.{this.MethodName}";
}

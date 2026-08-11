using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Net.HttpServer.Grpc.Internal;

namespace Shiny.Net.HttpServer.Grpc;

/// <summary>
/// The methods of one gRPC service. Each <c>Map</c> call adds a route at
/// <c>/{service}/{method}</c> — which is all a gRPC method is on the wire — and returns its route,
/// so a single method can carry its own policy while the builder covers the rest.
/// <code>
/// app.MapGrpcService("greet.Greeter", svc =>
/// {
///     svc.AddMarshaller&lt;HelloRequest&gt;(m => m.ToByteArray(), HelloRequest.Parser.ParseFrom);
///     svc.AddMarshaller&lt;HelloReply&gt;(m => m.ToByteArray(), HelloReply.Parser.ParseFrom);
///
///     svc.MapUnary&lt;HelloRequest, HelloReply&gt;("SayHello", (request, context) =>
///         new ValueTask&lt;HelloReply&gt;(new HelloReply { Message = $"Hello {request.Name}" }));
/// })
/// .RequireAuthorization();
/// </code>
/// </summary>
public sealed class GrpcServiceBuilder
{
    readonly IEndpointRouteBuilder endpoints;
    readonly List<RouteEndpointBuilder> routes = [];
    readonly ILogger logger;

    internal GrpcServiceBuilder(IEndpointRouteBuilder endpoints, string serviceName, GrpcOptions options)
    {
        this.endpoints = endpoints;
        this.ServiceName = serviceName;
        this.Options = options;

        this.logger = endpoints.Services?.GetService(typeof(ILoggerFactory)) is ILoggerFactory factory
            ? factory.CreateLogger("Shiny.Net.HttpServer.Grpc")
            : NullLogger.Instance;
    }

    /// <summary>The fully qualified service name, e.g. <c>greet.Greeter</c>.</summary>
    public string ServiceName { get; }

    /// <summary>Settings for every method of this service.</summary>
    public GrpcOptions Options { get; }

    /// <summary>The routes mapped so far, one per method.</summary>
    public IReadOnlyList<RouteEndpointBuilder> Routes => this.routes;

    /// <summary>Registers the marshaller for a message type used by this service.</summary>
    public GrpcServiceBuilder AddMarshaller<T>(GrpcMarshaller<T> marshaller)
    {
        this.Options.Marshallers.Add(marshaller);
        return this;
    }

    /// <summary>
    /// Registers a marshaller from a serializer and parser pair — the shape protobuf-generated
    /// messages already have as <c>ToByteArray()</c> and <c>Parser.ParseFrom(byte[])</c>.
    /// </summary>
    public GrpcServiceBuilder AddMarshaller<T>(Func<T, byte[]> serialize, Func<byte[], T> deserialize)
    {
        this.Options.Marshallers.Add(serialize, deserialize);
        return this;
    }

    /// <summary>One request, one response.</summary>
    public RouteEndpointBuilder MapUnary<TRequest, TResponse>(
        string method,
        GrpcUnaryHandler<TRequest, TResponse> handler
    )
    {
        ArgumentNullException.ThrowIfNull(handler);

        return this.Map<TRequest, TResponse>(
            method,
            (request, response) => new GrpcUnaryMethod<TRequest, TResponse>(request, response, handler)
        );
    }

    /// <summary>One request, a stream of responses.</summary>
    public RouteEndpointBuilder MapServerStreaming<TRequest, TResponse>(
        string method,
        GrpcServerStreamingHandler<TRequest, TResponse> handler
    )
    {
        ArgumentNullException.ThrowIfNull(handler);

        return this.Map<TRequest, TResponse>(
            method,
            (request, response) => new GrpcServerStreamingMethod<TRequest, TResponse>(request, response, handler)
        );
    }

    /// <summary>A stream of requests, one response.</summary>
    public RouteEndpointBuilder MapClientStreaming<TRequest, TResponse>(
        string method,
        GrpcClientStreamingHandler<TRequest, TResponse> handler
    )
    {
        ArgumentNullException.ThrowIfNull(handler);

        return this.Map<TRequest, TResponse>(
            method,
            (request, response) => new GrpcClientStreamingMethod<TRequest, TResponse>(request, response, handler)
        );
    }

    /// <summary>Streams in both directions at once.</summary>
    public RouteEndpointBuilder MapDuplexStreaming<TRequest, TResponse>(
        string method,
        GrpcDuplexStreamingHandler<TRequest, TResponse> handler
    )
    {
        ArgumentNullException.ThrowIfNull(handler);

        return this.Map<TRequest, TResponse>(
            method,
            (request, response) => new GrpcDuplexStreamingMethod<TRequest, TResponse>(request, response, handler)
        );
    }

    /// <summary>Requires authorization on every method mapped so far, optionally against named policies.</summary>
    public GrpcServiceBuilder RequireAuthorization(params string[] policies)
        => this.ForEach(route => route.RequireAuthorization(policies));

    /// <summary>Exempts every method mapped so far from authorization, including a fallback policy.</summary>
    public GrpcServiceBuilder AllowAnonymous()
        => this.ForEach(route => route.AllowAnonymous());

    /// <summary>Applies a CORS policy to every method — how a browser reaches a gRPC-Web service.</summary>
    public GrpcServiceBuilder RequireCors(string policyName)
        => this.ForEach(route => route.RequireCors(policyName));

    /// <summary>Applies a rate-limiting policy to every method mapped so far.</summary>
    public GrpcServiceBuilder RequireRateLimiting(string policyName)
        => this.ForEach(route => route.RequireRateLimiting(policyName));

    /// <summary>Runs <paramref name="configure"/> against every method mapped so far.</summary>
    public GrpcServiceBuilder ForEach(Action<RouteEndpointBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        foreach (var route in this.routes)
            configure(route);

        return this;
    }

    RouteEndpointBuilder Map<TRequest, TResponse>(
        string method,
        Func<GrpcMarshaller<TRequest>, GrpcMarshaller<TResponse>, GrpcMethodHandler> create
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        method = method.Trim('/');

        var full = $"/{this.ServiceName}/{method}";

        // Resolved now rather than per call: a missing marshaller is a wiring mistake, and it should
        // be a startup exception naming the method, not an error the first caller discovers.
        var handler = create(
            this.Options.Marshallers.Require<TRequest>(full),
            this.Options.Marshallers.Require<TResponse>(full)
        );

        var dispatcher = new GrpcCallDispatcher(this.ServiceName, method, handler, this.Options, this.logger);

        var route = this.endpoints
            .Map(HttpMethods.Post, "/" + method, dispatcher.InvokeAsync)
            .WithMetadata(new GrpcMethodMetadata(this.ServiceName, method))

            // gRPC describes itself with a .proto file. There is nothing useful for an OpenAPI
            // document to say about a route whose body is a length-prefixed protobuf frame.
            .ExcludeFromDescription();

        this.routes.Add(route);

        return route;
    }
}

/// <summary>
/// Attached to every gRPC route, so middleware — logging, metrics, authorization — can tell a gRPC
/// call from an HTTP one and name it without parsing the path.
/// </summary>
public sealed class GrpcMethodMetadata(string serviceName, string methodName)
{
    public string ServiceName { get; } = serviceName;
    public string MethodName { get; } = methodName;
    public string FullMethod => $"/{this.ServiceName}/{this.MethodName}";
}

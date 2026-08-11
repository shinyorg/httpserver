namespace Shiny.Net.HttpServer.Grpc;

/// <summary>
/// gRPC and gRPC-Web, served by the same HTTP/2 stack as everything else — no ASP.NET Core, and
/// nothing reflecting over your message types.
/// <para>
/// A gRPC method is a POST to <c>/{service}/{method}</c> whose body is a sequence of length-prefixed
/// messages, and whose outcome arrives in trailers after them. That last part is why this needs a
/// server that can send trailers, and why it works here.
/// </para>
/// <code>
/// app.MapGrpcService("greet.Greeter", svc =>
/// {
///     svc.AddMarshaller&lt;HelloRequest&gt;(m => m.ToByteArray(), HelloRequest.Parser.ParseFrom);
///     svc.AddMarshaller&lt;HelloReply&gt;(m => m.ToByteArray(), HelloReply.Parser.ParseFrom);
///
///     svc.MapUnary&lt;HelloRequest, HelloReply&gt;("SayHello", (request, context) =>
///         new ValueTask&lt;HelloReply&gt;(new HelloReply { Message = $"Hello {request.Name}" }));
///
///     svc.MapServerStreaming&lt;HelloRequest, HelloReply&gt;("Greetings", Greetings);
/// });
/// </code>
/// <para>
/// Callers reach it with <c>Grpc.Net.Client</c>, <c>grpcurl</c>, or any gRPC client in any language.
/// Over cleartext they need HTTP/2 without TLS — <c>options.Http2.AllowCleartext</c> here, and
/// <c>AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true)</c>
/// on a .NET client. Browsers use gRPC-Web, which is on by default and needs a CORS policy that
/// exposes <c>grpc-status</c> and <c>grpc-message</c>.
/// </para>
/// </summary>
public static class GrpcEndpoints
{
    /// <summary>Maps a gRPC service and its methods.</summary>
    public static GrpcServiceBuilder MapGrpcService(
        this HttpServer server,
        string serviceName,
        Action<GrpcServiceBuilder> configure
    )
    {
        ArgumentNullException.ThrowIfNull(server);

        return server.MapGrpcService(serviceName, new GrpcOptions(), configure);
    }

    /// <summary>Maps a gRPC service from options built elsewhere — shared marshallers, shared limits.</summary>
    public static GrpcServiceBuilder MapGrpcService(
        this HttpServer server,
        string serviceName,
        GrpcOptions options,
        Action<GrpcServiceBuilder> configure
    )
    {
        ArgumentNullException.ThrowIfNull(server);

        GrpcServiceBuilder? builder = null;
        server.MapGroup(NormalizeService(serviceName), group => builder = Map(group, serviceName, options, configure));

        return builder!;
    }

    /// <summary>Maps a gRPC service onto an existing route builder, inside whatever prefix it carries.</summary>
    public static GrpcServiceBuilder MapGrpcService(
        this IEndpointRouteBuilder endpoints,
        string serviceName,
        Action<GrpcServiceBuilder> configure
    ) => endpoints.MapGrpcService(serviceName, new GrpcOptions(), configure);

    /// <summary>Maps a gRPC service onto an existing route builder, from options built elsewhere.</summary>
    public static GrpcServiceBuilder MapGrpcService(
        this IEndpointRouteBuilder endpoints,
        string serviceName,
        GrpcOptions options,
        Action<GrpcServiceBuilder> configure
    )
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return Map(endpoints.MapGroup(NormalizeService(serviceName)), serviceName, options, configure);
    }

    static GrpcServiceBuilder Map(
        IEndpointRouteBuilder group,
        string serviceName,
        GrpcOptions options,
        Action<GrpcServiceBuilder> configure
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new GrpcServiceBuilder(group, NormalizeService(serviceName).Trim('/'), options);
        configure(builder);

        return builder;
    }

    static string NormalizeService(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        return "/" + serviceName.Trim().Trim('/');
    }
}

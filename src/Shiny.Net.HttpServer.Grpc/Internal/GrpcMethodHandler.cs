using System.IO.Pipelines;

namespace Shiny.Net.HttpServer.Grpc.Internal;

/// <summary>What the dispatcher hands a method: the two ends of the call, already framed.</summary>
sealed class GrpcCallEnvironment(PipeReader reader, GrpcFrameWriter writer, string requestEncoding, int? maxReceiveMessageSize)
{
    public PipeReader Reader { get; } = reader;
    public GrpcFrameWriter Writer { get; } = writer;
    public string RequestEncoding { get; } = requestEncoding;
    public int? MaxReceiveMessageSize { get; } = maxReceiveMessageSize;
}

/// <summary>
/// One mapped method, with its marshallers and handler already bound. The generic message types
/// stop here — the dispatcher above deals only in frames, so nothing it does has to know them.
/// </summary>
abstract class GrpcMethodHandler
{
    /// <summary>
    /// Whether a gRPC-Web caller can reach this method. The web protocol has no way to send more
    /// than one request message, so client-streaming and duplex methods are unreachable from it.
    /// </summary>
    public abstract bool SupportsWeb { get; }

    public abstract ValueTask InvokeAsync(
        GrpcCallContext context,
        GrpcCallEnvironment environment,
        CancellationToken cancellationToken
    );
}

sealed class GrpcUnaryMethod<TRequest, TResponse>(
    GrpcMarshaller<TRequest> requestMarshaller,
    GrpcMarshaller<TResponse> responseMarshaller,
    GrpcUnaryHandler<TRequest, TResponse> handler
) : GrpcMethodHandler
{
    public override bool SupportsWeb => true;

    public override async ValueTask InvokeAsync(
        GrpcCallContext context,
        GrpcCallEnvironment environment,
        CancellationToken cancellationToken
    )
    {
        var reader = new GrpcFrameReader<TRequest>(
            environment.Reader,
            requestMarshaller,
            environment.RequestEncoding,
            environment.MaxReceiveMessageSize
        );

        var request = await reader.ReadSingleAsync(cancellationToken).ConfigureAwait(false);
        var response = await handler(request, context).ConfigureAwait(false);

        await environment.Writer.WriteMessageAsync(responseMarshaller, response, cancellationToken).ConfigureAwait(false);
    }
}

sealed class GrpcServerStreamingMethod<TRequest, TResponse>(
    GrpcMarshaller<TRequest> requestMarshaller,
    GrpcMarshaller<TResponse> responseMarshaller,
    GrpcServerStreamingHandler<TRequest, TResponse> handler
) : GrpcMethodHandler
{
    public override bool SupportsWeb => true;

    public override async ValueTask InvokeAsync(
        GrpcCallContext context,
        GrpcCallEnvironment environment,
        CancellationToken cancellationToken
    )
    {
        var reader = new GrpcFrameReader<TRequest>(
            environment.Reader,
            requestMarshaller,
            environment.RequestEncoding,
            environment.MaxReceiveMessageSize
        );

        var request = await reader.ReadSingleAsync(cancellationToken).ConfigureAwait(false);

        await foreach (var response in handler(request, context).WithCancellation(cancellationToken).ConfigureAwait(false))
            await environment.Writer.WriteMessageAsync(responseMarshaller, response, cancellationToken).ConfigureAwait(false);
    }
}

sealed class GrpcClientStreamingMethod<TRequest, TResponse>(
    GrpcMarshaller<TRequest> requestMarshaller,
    GrpcMarshaller<TResponse> responseMarshaller,
    GrpcClientStreamingHandler<TRequest, TResponse> handler
) : GrpcMethodHandler
{
    public override bool SupportsWeb => false;

    public override async ValueTask InvokeAsync(
        GrpcCallContext context,
        GrpcCallEnvironment environment,
        CancellationToken cancellationToken
    )
    {
        var reader = new GrpcFrameReader<TRequest>(
            environment.Reader,
            requestMarshaller,
            environment.RequestEncoding,
            environment.MaxReceiveMessageSize
        );

        var response = await handler(reader.ReadAllAsync(cancellationToken), context).ConfigureAwait(false);

        await environment.Writer.WriteMessageAsync(responseMarshaller, response, cancellationToken).ConfigureAwait(false);
    }
}

sealed class GrpcDuplexStreamingMethod<TRequest, TResponse>(
    GrpcMarshaller<TRequest> requestMarshaller,
    GrpcMarshaller<TResponse> responseMarshaller,
    GrpcDuplexStreamingHandler<TRequest, TResponse> handler
) : GrpcMethodHandler
{
    public override bool SupportsWeb => false;

    public override async ValueTask InvokeAsync(
        GrpcCallContext context,
        GrpcCallEnvironment environment,
        CancellationToken cancellationToken
    )
    {
        var reader = new GrpcFrameReader<TRequest>(
            environment.Reader,
            requestMarshaller,
            environment.RequestEncoding,
            environment.MaxReceiveMessageSize
        );

        var responses = handler(reader.ReadAllAsync(cancellationToken), context);

        await foreach (var response in responses.WithCancellation(cancellationToken).ConfigureAwait(false))
            await environment.Writer.WriteMessageAsync(responseMarshaller, response, cancellationToken).ConfigureAwait(false);
    }
}

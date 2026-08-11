namespace Shiny.Net.HttpServer.Grpc;

/// <summary>One request, one response. The shape most methods have.</summary>
public delegate ValueTask<TResponse> GrpcUnaryHandler<in TRequest, TResponse>(
    TRequest request,
    GrpcCallContext context
);

/// <summary>
/// One request, a stream of responses. Each message is flushed as the enumerator yields it, so a
/// handler that awaits between items is a live feed rather than a batch delivered at the end.
/// </summary>
public delegate IAsyncEnumerable<TResponse> GrpcServerStreamingHandler<in TRequest, out TResponse>(
    TRequest request,
    GrpcCallContext context
);

/// <summary>
/// A stream of requests, one response. The enumerable yields messages as they arrive off the wire;
/// enumerating it to the end is what waits for the caller to finish sending.
/// </summary>
public delegate ValueTask<TResponse> GrpcClientStreamingHandler<TRequest, TResponse>(
    IAsyncEnumerable<TRequest> requests,
    GrpcCallContext context
);

/// <summary>
/// Streams both ways at once. Nothing sequences the two sides: a handler may answer before it has
/// read everything, read everything before it answers, or interleave — the HTTP/2 stream underneath
/// carries both directions independently.
/// </summary>
public delegate IAsyncEnumerable<TResponse> GrpcDuplexStreamingHandler<TRequest, out TResponse>(
    IAsyncEnumerable<TRequest> requests,
    GrpcCallContext context
);

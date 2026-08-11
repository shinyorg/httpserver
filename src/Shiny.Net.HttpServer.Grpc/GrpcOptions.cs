namespace Shiny.Net.HttpServer.Grpc;

/// <summary>
/// Settings shared by every method in one <c>MapGrpcService</c> call.
/// </summary>
public sealed class GrpcOptions
{
    /// <summary>
    /// Largest message this server will accept, in bytes. Default 4MB, matching every other gRPC
    /// implementation. A larger message is refused with <see cref="GrpcStatusCode.ResourceExhausted"/>
    /// on its length prefix, before the bytes are read. Null removes the limit — on a device, that
    /// means one caller can decide how much memory the app allocates.
    /// </summary>
    public int? MaxReceiveMessageSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Largest message this server will send, in bytes. Null (the default) leaves it unbounded,
    /// since what the server sends is the server's own doing.
    /// </summary>
    public int? MaxSendMessageSize { get; set; }

    /// <summary>
    /// Compression applied to response messages — "gzip", "deflate", or null for none (the default).
    /// It is used only when the caller's <c>grpc-accept-encoding</c> includes it, and only for
    /// messages it actually makes smaller.
    /// </summary>
    public string? ResponseCompression { get; set; }

    /// <summary>
    /// Whether the message of an unhandled exception is sent to the caller in <c>grpc-message</c>.
    /// Off by default: an exception message is written for whoever reads the logs, and it routinely
    /// names paths, connection strings and internal types. Handled failures — anything thrown as a
    /// <see cref="GrpcStatusException"/> — are always reported in full, whatever this says.
    /// </summary>
    public bool EnableDetailedErrors { get; set; }

    /// <summary>
    /// Whether callers may use gRPC-Web, which carries the same calls over HTTP/1.1 and is the only
    /// form a browser can make. On by default; it costs nothing until a request arrives with a
    /// gRPC-Web content type.
    /// <para>
    /// gRPC-Web has no client-streaming or bidirectional calls — the protocol cannot express them —
    /// so those methods answer <see cref="GrpcStatusCode.Unimplemented"/> to a web caller and work
    /// normally for everyone else.
    /// </para>
    /// </summary>
    public bool EnableGrpcWeb { get; set; } = true;

    /// <summary>
    /// Marshallers available to every method of the service. Methods are mapped against this, so a
    /// message type has to be registered before the method that uses it is mapped.
    /// </summary>
    public GrpcMarshallerRegistry Marshallers { get; } = new();
}

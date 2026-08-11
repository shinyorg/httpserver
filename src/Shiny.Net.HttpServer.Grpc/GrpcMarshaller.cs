using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Shiny.Net.HttpServer.Grpc;

/// <summary>
/// Turns one message type into bytes and back.
/// <para>
/// Serialization is supplied rather than discovered. Nothing in this package knows what protobuf is,
/// reflects over your message types, or drags a serializer into your app that you did not ask for —
/// which is also why the whole path stays trim- and AOT-clean. Hand it the two functions your
/// generated code already exposes:
/// </para>
/// <code>
/// // Google.Protobuf generated messages
/// var marshaller = GrpcMarshaller.Create&lt;HelloRequest&gt;(m => m.ToByteArray(), HelloRequest.Parser.ParseFrom);
///
/// // or System.Text.Json, source-generated
/// var marshaller = GrpcMarshaller.Json(AppJsonContext.Default.HelloRequest);
/// </code>
/// </summary>
public abstract class GrpcMarshaller<T>
{
    /// <summary>Writes <paramref name="value"/> as the payload of one message frame.</summary>
    public abstract void Write(T value, IBufferWriter<byte> writer);

    /// <summary>Reads one message frame's payload. The sequence is only valid for this call.</summary>
    public abstract T Read(ReadOnlySequence<byte> payload);
}

/// <summary>Factories for the marshaller shapes that generated code already fits.</summary>
public static class GrpcMarshaller
{
    /// <summary>
    /// A marshaller from a byte-array serializer and parser — the pair every protobuf-generated
    /// message type exposes as <c>ToByteArray()</c> and <c>Parser.ParseFrom(byte[])</c>.
    /// </summary>
    public static GrpcMarshaller<T> Create<T>(Func<T, byte[]> serialize, Func<byte[], T> deserialize)
    {
        ArgumentNullException.ThrowIfNull(serialize);
        ArgumentNullException.ThrowIfNull(deserialize);

        return new DelegateMarshaller<T>(
            (value, writer) => writer.Write(serialize(value)),
            payload => deserialize(payload.ToArray())
        );
    }

    /// <summary>
    /// A marshaller that writes straight into the connection's buffer and reads from it in place,
    /// for serializers that can work against <see cref="IBufferWriter{T}"/> and
    /// <see cref="ReadOnlySequence{T}"/> without a byte[] in the middle.
    /// </summary>
    public static GrpcMarshaller<T> Create<T>(
        Action<T, IBufferWriter<byte>> serialize,
        Func<ReadOnlySequence<byte>, T> deserialize
    )
    {
        ArgumentNullException.ThrowIfNull(serialize);
        ArgumentNullException.ThrowIfNull(deserialize);

        return new DelegateMarshaller<T>(serialize, deserialize);
    }

    /// <summary>
    /// A JSON marshaller over a source-generated <see cref="JsonTypeInfo{T}"/>, for services whose
    /// callers speak <c>application/grpc+json</c> rather than protobuf. Taking the type info rather
    /// than the type keeps reflection — and the trim warning that comes with it — out of the path.
    /// </summary>
    public static GrpcMarshaller<T> Json<T>(JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        return new DelegateMarshaller<T>(
            (value, writer) =>
            {
                using var json = new Utf8JsonWriter(writer);
                JsonSerializer.Serialize(json, value, typeInfo);
            },
            payload =>
            {
                var reader = new Utf8JsonReader(payload);
                return JsonSerializer.Deserialize(ref reader, typeInfo)!;
            }
        );
    }

    sealed class DelegateMarshaller<T>(
        Action<T, IBufferWriter<byte>> serialize,
        Func<ReadOnlySequence<byte>, T> deserialize
    ) : GrpcMarshaller<T>
    {
        public override void Write(T value, IBufferWriter<byte> writer) => serialize(value, writer);

        public override T Read(ReadOnlySequence<byte> payload) => deserialize(payload);
    }
}

/// <summary>
/// The marshallers a service knows about, looked up by message type when a method is mapped.
/// <para>
/// Registering per type rather than per method is the whole point: a service with twenty methods
/// over eight message types names each type once, and a missing one is reported at startup by the
/// <c>Map</c> call that needed it — not on the first call that tries to decode a message.
/// </para>
/// </summary>
public sealed class GrpcMarshallerRegistry
{
    readonly Dictionary<Type, object> marshallers = [];

    /// <summary>Registers a marshaller for <typeparamref name="T"/>, replacing any previous one.</summary>
    public GrpcMarshallerRegistry Add<T>(GrpcMarshaller<T> marshaller)
    {
        ArgumentNullException.ThrowIfNull(marshaller);

        this.marshallers[typeof(T)] = marshaller;
        return this;
    }

    /// <summary>Registers a marshaller built from a serializer and parser pair.</summary>
    public GrpcMarshallerRegistry Add<T>(Func<T, byte[]> serialize, Func<byte[], T> deserialize)
        => this.Add(GrpcMarshaller.Create(serialize, deserialize));

    /// <summary>The marshaller for <typeparamref name="T"/>, or null when none was registered.</summary>
    public GrpcMarshaller<T>? Find<T>()
        => this.marshallers.TryGetValue(typeof(T), out var found) ? (GrpcMarshaller<T>)found : null;

    /// <summary>Copies every registration from <paramref name="other"/> that is not already present.</summary>
    internal void MergeMissingFrom(GrpcMarshallerRegistry other)
    {
        foreach (var (type, marshaller) in other.marshallers)
            this.marshallers.TryAdd(type, marshaller);
    }

    internal GrpcMarshaller<T> Require<T>(string method)
        => this.Find<T>() ?? throw new InvalidOperationException(
            $"No gRPC marshaller is registered for {typeof(T).Name}, needed by '{method}'. "
            + $"Register one with AddMarshaller<{typeof(T).Name}>(...) before mapping the method."
        );
}

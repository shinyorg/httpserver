using Microsoft.Extensions.DependencyInjection;

namespace Shiny.Net.HttpServer.Negotiation;

/// <summary>
/// The codecs a binary format knows about, looked up by message type.
/// <para>
/// Serialization is supplied rather than discovered — the same bargain the gRPC package strikes, for
/// the same reason. Protobuf cannot be produced without a schema: field numbers and wire types live
/// in the <c>.proto</c>, and the only thing that has them is the code <c>protoc</c> already
/// generated. Reaching them at runtime would mean reflecting over your message types and dragging a
/// serializer into this assembly that half its users do not want. So hand over the pair your
/// generated code already exposes:
/// </para>
/// <code>
/// options.AddProtobuf(p => p
///     .Add&lt;Reading&gt;(m => m.ToByteArray(), Reading.Parser.ParseFrom)
///     .Add&lt;ReadingList&gt;(m => m.ToByteArray(), ReadingList.Parser.ParseFrom));
/// </code>
/// <para>
/// Nothing here is protobuf-specific. The same registry carries MessagePack-CSharp's native codec,
/// CBOR, Avro, or a hand-rolled encoding, under whichever media type you register it as.
/// </para>
/// </summary>
public sealed class BinaryCodecRegistry
{
    readonly Dictionary<Type, Codec> codecs = [];

    /// <summary>Registers a codec for <typeparamref name="T"/> in both directions.</summary>
    public BinaryCodecRegistry Add<T>(Func<T, byte[]> serialize, Func<byte[], T> deserialize)
    {
        ArgumentNullException.ThrowIfNull(serialize);
        ArgumentNullException.ThrowIfNull(deserialize);

        this.codecs[typeof(T)] = new Codec(
            value => serialize((T)value),
            bytes => deserialize(bytes)!
        );

        return this;
    }

    /// <summary>
    /// Registers a write-only codec, for a type that is only ever a response. A request body of this
    /// type answers 415 rather than pretending.
    /// </summary>
    public BinaryCodecRegistry AddWriteOnly<T>(Func<T, byte[]> serialize)
    {
        ArgumentNullException.ThrowIfNull(serialize);

        this.codecs[typeof(T)] = new Codec(value => serialize((T)value), null);
        return this;
    }

    /// <summary>
    /// Registers a read-only codec, for a type that is only ever a request body. Returning this type
    /// from an endpoint negotiates away to another format.
    /// </summary>
    public BinaryCodecRegistry AddReadOnly<T>(Func<byte[], T> deserialize)
    {
        ArgumentNullException.ThrowIfNull(deserialize);

        this.codecs[typeof(T)] = new Codec(null, bytes => deserialize(bytes)!);
        return this;
    }

    internal bool CanWrite(Type type) => this.codecs.TryGetValue(type, out var codec) && codec.Write is not null;

    internal bool CanRead(Type type) => this.codecs.TryGetValue(type, out var codec) && codec.Read is not null;

    internal Func<object, byte[]> RequireWrite(Type type)
        => this.codecs.TryGetValue(type, out var codec) && codec.Write is { } write
            ? write
            : throw Missing(type, "serializer");

    internal Func<byte[], object> RequireRead(Type type)
        => this.codecs.TryGetValue(type, out var codec) && codec.Read is { } read
            ? read
            : throw Missing(type, "parser");

    static InvalidOperationException Missing(Type type, string what) => new(
        $"No {what} is registered for '{type.FullName}'. "
            + $"Register one with .Add<{type.Name}>(...) where the binary format is configured."
    );

    sealed record Codec(Func<object, byte[]>? Write, Func<byte[], object>? Read);
}

/// <summary>
/// Writes a binary representation whose codec the app supplied.
/// <para>
/// A type gets this representation because a codec was registered for it — the same rule the JSON
/// formatter follows with metadata, so an unregistered type negotiates away to another format
/// instead of failing at serialization time.
/// </para>
/// </summary>
public sealed class BinaryOutputFormatter(string mediaType, BinaryCodecRegistry codecs, int priority = 45)
    : IOutputFormatter
{
    readonly BinaryCodecRegistry codecs = codecs ?? throw new ArgumentNullException(nameof(codecs));

    public string MediaType { get; } = mediaType ?? throw new ArgumentNullException(nameof(mediaType));

    public int Priority { get; } = priority;

    /// <summary>None. These are bytes, not text in an encoding.</summary>
    public string? Charset => null;

    public bool CanWrite(object? value) => value is null || this.codecs.CanWrite(value.GetType());

    public async ValueTask WriteAsync(HttpContext context, object? value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Protobuf has no null: a message with every field at its default encodes to zero bytes, and
        // that is the closest thing the format has to say.
        var bytes = value is null
            ? []
            : this.codecs.RequireWrite(value.GetType())(value);

        await context.Response.WriteBytesAsync(bytes, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Reads a binary request body with a codec the app supplied.</summary>
public sealed class BinaryInputFormatter(string mediaType, BinaryCodecRegistry codecs, int priority = 45)
    : IInputFormatter
{
    readonly BinaryCodecRegistry codecs = codecs ?? throw new ArgumentNullException(nameof(codecs));

    public string MediaType { get; } = mediaType ?? throw new ArgumentNullException(nameof(mediaType));

    public int Priority { get; } = priority;

    /// <summary>Other spellings of the same format that this formatter also reads.</summary>
    public IList<string> AlternateMediaTypes { get; } = [];

    /// <summary>How much body to buffer before refusing with a 413.</summary>
    public int MaxBodyBytes { get; set; } = 1024 * 1024;

    public bool CanRead(string mediaType, Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);

        if (!this.codecs.CanRead(targetType))
            return false;

        if (this.MediaType.Equals(mediaType, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var alternate in this.AlternateMediaTypes)
        {
            if (alternate.Equals(mediaType, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public async ValueTask<InputFormatterResult> ReadAsync(
        HttpContext context,
        Type targetType,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(targetType);

        var read = this.codecs.RequireRead(targetType);

        var body = await context.Request
            .ReadBodyAsBytesAsync(this.MaxBodyBytes, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            return InputFormatterResult.FromValue(read(body));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Wide on purpose. The codec is the app's, and every serializer spells "those bytes are
            // not a valid message" as a different exception type — Google.Protobuf's
            // InvalidProtocolBufferException, MessagePack's MessagePackSerializationException, and so
            // on. All of them describe a bad request, and letting any of them escape would report the
            // client's mistake as a 500.
            return InputFormatterResult.Malformed;
        }
    }
}

/// <summary>Registering a binary format whose codec the app supplies.</summary>
public static class BinaryFormatterExtensions
{
    /// <summary>The spelling nearly every protobuf-over-HTTP client sends.</summary>
    public const string ProtobufMediaType = "application/x-protobuf";

    /// <summary>Other spellings of protobuf, all read on the way in.</summary>
    public static readonly string[] ProtobufAlternateMediaTypes =
    [
        "application/protobuf",
        "application/vnd.google.protobuf"
    ];

    /// <summary>
    /// Adds protobuf in both directions, over codecs from your generated message types.
    /// <code>
    /// builder.Services.AddContentNegotiation(o => o
    ///     .AddProtobuf(p => p.Add&lt;Reading&gt;(m => m.ToByteArray(), Reading.Parser.ParseFrom))
    /// );
    /// </code>
    /// </summary>
    public static ContentNegotiationOptions AddProtobuf(
        this ContentNegotiationOptions options,
        Action<BinaryCodecRegistry> configure
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configure);

        var codecs = new BinaryCodecRegistry();
        configure(codecs);

        return options.AddBinaryFormat(ProtobufMediaType, codecs, alternateMediaTypes: ProtobufAlternateMediaTypes);
    }

    /// <summary>
    /// Adds a binary format under <paramref name="mediaType"/>, plus any other spellings of it that
    /// should be read on the way in.
    /// <para>
    /// The alternates are read but never written: a response has to pick one content type, and
    /// picking the registered spelling is the only choice that does not surprise someone.
    /// </para>
    /// </summary>
    public static ContentNegotiationOptions AddBinaryFormat(
        this ContentNegotiationOptions options,
        string mediaType,
        BinaryCodecRegistry codecs,
        int priority = 45,
        params string[] alternateMediaTypes
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(codecs);

        var input = new BinaryInputFormatter(mediaType, codecs, priority);

        foreach (var alternate in alternateMediaTypes ?? [])
            input.AlternateMediaTypes.Add(alternate);

        return options.Add(new BinaryOutputFormatter(mediaType, codecs, priority), input);
    }

    /// <summary>Registers content negotiation with protobuf added, for an app that wants nothing else.</summary>
    public static ShinyHttpServerBuilder AddProtobufFormatters(
        this ShinyHttpServerBuilder builder,
        Action<BinaryCodecRegistry> configure
    ) => builder.AddContentNegotiation(o => o.AddProtobuf(configure));
}

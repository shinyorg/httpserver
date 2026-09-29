using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Shiny.Net.HttpServer.Switchboard.Internal;

/// <summary>
/// The wire protocol, version 1. One event stream down (<c>GET {base}/connect</c>), plain POSTs up
/// (<c>POST {base}/invoke</c>), a DELETE to hang up from the client side.
/// </summary>
static class SwitchboardProtocol
{
    public const int Version = 1;

    /// <summary>Carries the line token on every upstream request.</summary>
    public const string LineHeader = "X-Switchboard-Line";

    /// <summary>
    /// The line token on a resuming connect, for a client that cannot set headers on a stream. The
    /// <see cref="LineHeader"/> is preferred: query strings are logged.
    /// </summary>
    public const string TokenQuery = "token";

    /// <summary>The last event id on a resuming connect, for the same reason.</summary>
    public const string LastEventIdQuery = "lastEventId";

    public const string ConnectPath = "connect";
    public const string InvokePath = "invoke";
    public const string CompletionPath = "completion";

    // ---- events on the line's stream ----
    public const string ConnectedEvent = "connected";
    public const string ResumedEvent = "resumed";
    public const string InvokeEvent = "invoke";
    public const string CloseEvent = "close";

    // ---- events on a streaming invocation's response ----
    public const string StreamItemEvent = "item";
    public const string StreamCompleteEvent = "complete";
    public const string StreamErrorEvent = "error";

    public const string GenericError = "An unexpected error occurred invoking the method.";
}

sealed record ConnectedPayload(int Protocol, string LineId, string LineToken, long HeartbeatMs, long ResumeWindowMs);

sealed record ResumedPayload(string LineId, bool Missed);

sealed record ClosePayload(string? Reason, bool AllowReconnect);

sealed record ErrorPayload(string Error);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ConnectedPayload))]
[JsonSerializable(typeof(ResumedPayload))]
[JsonSerializable(typeof(ClosePayload))]
[JsonSerializable(typeof(ErrorPayload))]
partial class SwitchboardProtocolJson : JsonSerializerContext;

/// <summary>
/// Metadata for the types an argument list is most often made of, so a method taking
/// <c>(string room, int take)</c> works without the app having to list <c>string</c> and <c>int</c>
/// in its own context. Consulted only after the app's registered contexts, which win.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(byte))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(char))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(DateOnly))]
[JsonSerializable(typeof(TimeOnly))]
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(bool?))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(long?))]
[JsonSerializable(typeof(double?))]
[JsonSerializable(typeof(decimal?))]
[JsonSerializable(typeof(Guid?))]
[JsonSerializable(typeof(DateTime?))]
[JsonSerializable(typeof(DateTimeOffset?))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int[]))]
[JsonSerializable(typeof(long[]))]
[JsonSerializable(typeof(Guid[]))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<int>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(JsonElement))]
partial class SwitchboardPrimitivesJson : JsonSerializerContext;

/// <summary>
/// Finds JSON metadata for argument and result types: the app's registered contexts first, then
/// the built-in primitives. Never reflection — a type nobody described is an error naming the fix.
/// </summary>
static class SwitchboardJson
{
    public static JsonTypeInfo<T> Get<T>()
    {
        if (JsonTypeInfoRegistry.TryGet<T>(out var registered))
            return registered;

        if (SwitchboardPrimitivesJson.Default.GetTypeInfo(typeof(T)) is JsonTypeInfo<T> primitive)
            return primitive;

        return JsonTypeInfoRegistry.GetRequired<T>();
    }

    public static JsonTypeInfo Get(Type type)
    {
        if (JsonTypeInfoRegistry.TryGet(type, out var registered))
            return registered;

        return SwitchboardPrimitivesJson.Default.GetTypeInfo(type) ?? JsonTypeInfoRegistry.GetRequired(type);
    }
}

/// <summary>Renders <c>{"target": …, "args": […]}</c> once, for however many lines receive it.</summary>
static class InvocationPayload
{
    public static string Write(string method, object?[]? args, string? invocationId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);

        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("target", method);
            writer.WriteStartArray("args");

            if (args is not null)
            {
                foreach (var arg in args)
                {
                    if (arg is null)
                        writer.WriteNullValue();
                    else
                        JsonSerializer.Serialize(writer, arg, SwitchboardJson.Get(arg.GetType()));
                }
            }

            writer.WriteEndArray();

            // Present only when the server is waiting for an answer, which is how the client knows
            // to post one back.
            if (invocationId is not null)
                writer.WriteString("invocationId", invocationId);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

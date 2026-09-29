using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Shiny.Net.HttpServer.Switchboard.Client.Internal;

/// <summary>
/// JSON metadata for a line: the app's contexts first, asked directly so their own options (casing
/// and the like) apply exactly as they do on the server; then plain resolvers; then primitives.
/// Never reflection.
/// </summary>
sealed class LineJson
{
    readonly JsonSerializerContext[] contexts;
    readonly JsonSerializerOptions? resolverOptions;
    readonly ConcurrentDictionary<Type, JsonTypeInfo> cache = new();

    public LineJson(IReadOnlyList<IJsonTypeInfoResolver> resolvers)
    {
        this.contexts = [.. resolvers.OfType<JsonSerializerContext>()];

        var plain = resolvers.Where(r => r is not JsonSerializerContext).ToArray();
        if (plain.Length > 0)
        {
            this.resolverOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                TypeInfoResolver = JsonTypeInfoResolver.Combine(plain)
            };
        }
    }

    public JsonTypeInfo<T> Get<T>() => (JsonTypeInfo<T>)this.Get(typeof(T));

    public JsonTypeInfo Get(Type type) => this.cache.GetOrAdd(type, this.Find);

    JsonTypeInfo Find(Type type)
    {
        foreach (var context in this.contexts)
        {
            if (context.GetTypeInfo(type) is { } fromContext)
                return fromContext;
        }

        if (this.resolverOptions is { } options && options.TryGetTypeInfo(type, out var fromResolver))
            return fromResolver;

        return ClientPrimitivesJson.Default.GetTypeInfo(type)
            ?? throw new InvalidOperationException(
                $"No JSON metadata is registered for '{type.FullName}'. Add [JsonSerializable(typeof({type.Name}))] " +
                "to a JsonSerializerContext and pass it to SwitchboardLineBuilder.WithJson(...)."
            );
    }

    /// <summary>Writes positional arguments, each with the metadata for its runtime type.</summary>
    public void WriteArguments(Utf8JsonWriter writer, object?[]? args)
    {
        writer.WriteStartArray("args");

        if (args is not null)
        {
            foreach (var arg in args)
            {
                if (arg is null)
                    writer.WriteNullValue();
                else
                    JsonSerializer.Serialize(writer, arg, this.Get(arg.GetType()));
            }
        }

        writer.WriteEndArray();
    }
}

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
partial class ClientPrimitivesJson : JsonSerializerContext;

/// <summary>One event from a <c>text/event-stream</c>.</summary>
readonly record struct StreamEvent(string? Event, string? Id, string Data);

/// <summary>
/// Reads a <c>text/event-stream</c> one dispatched event at a time. Every line read — heartbeat
/// comments included — counts as activity, which is what the line's dead-link watchdog feeds on.
/// </summary>
sealed class EventStreamReader(Stream stream, Action? onActivity = null) : IDisposable
{
    readonly StreamReader reader = new(stream, Encoding.UTF8);

    /// <summary>The next event, or null at the end of the stream.</summary>
    public async ValueTask<StreamEvent?> ReadAsync(CancellationToken cancellationToken)
    {
        string? name = null;
        string? id = null;
        StringBuilder? data = null;

        while (true)
        {
            var line = await this.reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                return null;

            onActivity?.Invoke();

            if (line.Length == 0)
            {
                if (name is null && id is null && data is null)
                    continue;

                return new StreamEvent(name, id, data?.ToString() ?? string.Empty);
            }

            if (line[0] == ':')
                continue;

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..];
            if (value.StartsWith(' '))
                value = value[1..];

            switch (field)
            {
                case "event":
                    name = value;
                    break;

                case "id":
                    id = value;
                    break;

                case "data":
                    data = data is null ? new StringBuilder(value) : data.Append('\n').Append(value);
                    break;
            }
        }
    }

    public void Dispose() => this.reader.Dispose();
}

/// <summary>Remembers the last N ids seen, so a question replayed after a resume is answered once.</summary>
sealed class RecentIds(int capacity)
{
    readonly HashSet<string> set = new(StringComparer.Ordinal);
    readonly Queue<string> order = new();

    public bool Add(string id)
    {
        lock (this.set)
        {
            if (!this.set.Add(id))
                return false;

            this.order.Enqueue(id);
            if (this.order.Count > capacity)
                this.set.Remove(this.order.Dequeue());

            return true;
        }
    }
}

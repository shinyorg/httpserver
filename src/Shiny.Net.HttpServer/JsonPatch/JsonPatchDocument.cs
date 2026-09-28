using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Shiny.Net.HttpServer.JsonPatch;

/// <summary>
/// An RFC 6902 JSON Patch — an ordered list of operations applied to a JSON document all-or-nothing.
/// <para>
/// Where merge patch (RFC 7396) can only say "this is what the object should look like", JSON Patch can
/// say things merge patch cannot: append to an array (<c>/items/-</c>), remove an array element, move
/// or copy a value, set a member to JSON <c>null</c> rather than deleting it, and — the one that makes
/// it safe for concurrent editing — <c>test</c> a value first and abort if it is not what the client
/// last saw.
/// </para>
/// <para>
/// Built on <see cref="System.Text.Json.Nodes"/>, with no reflection and no <see cref="JsonSerializer"/>
/// call that is not handed a <see cref="JsonTypeInfo{T}"/>, so it is trim- and AOT-clean. Applying
/// never touches the document it is given: it works on a copy and hands back the result only when every
/// operation succeeded, which is RFC 6902 §5's atomicity for free.
/// </para>
/// <code>
/// app.MapPatch("/notes/{id}", async ctx =>
/// {
///     var patch = await ctx.Request.ReadJsonPatchAsync();
///     var note = notes.Get(id);
///     notes.Save(patch.ApplyTo(note, AppJson.Default.Note));   // throws JsonPatchException → 400/409/422
///     return Results.NoContent();
/// });
/// </code>
/// </summary>
[JsonConverter(typeof(JsonPatchDocumentConverter))]
public sealed class JsonPatchDocument
{
    /// <summary>The media type RFC 6902 registers: <c>application/json-patch+json</c>.</summary>
    public const string MediaType = "application/json-patch+json";

    /// <summary>The RFC 5789 header advertising which patch formats a resource accepts.</summary>
    public const string AcceptPatchHeader = "Accept-Patch";

    readonly List<JsonPatchOperation> operations;

    public JsonPatchDocument() => this.operations = [];

    public JsonPatchDocument(IEnumerable<JsonPatchOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        this.operations = [.. operations];
    }

    /// <summary>The operations, in the order they are applied.</summary>
    public IReadOnlyList<JsonPatchOperation> Operations => this.operations;

    // ── Building ────────────────────────────────────────────────────────
    // The same verbs, argument order included, as ASP.NET Core's JsonPatchDocument — but over pointers
    // rather than expression trees, since walking an expression to a property name is reflection.

    /// <summary>Adds (or, for an existing object member, replaces) <paramref name="value"/> at <paramref name="path"/>. <c>/-</c> appends to an array.</summary>
    public JsonPatchDocument Add(string path, JsonNode? value) => this.Append(() => JsonPatchOperation.Add(Pointer(path), value));

    public JsonPatchDocument Remove(string path) => this.Append(() => JsonPatchOperation.Remove(Pointer(path)));

    public JsonPatchDocument Replace(string path, JsonNode? value) => this.Append(() => JsonPatchOperation.Replace(Pointer(path), value));

    public JsonPatchDocument Move(string from, string path) => this.Append(() => JsonPatchOperation.Move(Pointer(from), Pointer(path)));

    public JsonPatchDocument Copy(string from, string path) => this.Append(() => JsonPatchOperation.Copy(Pointer(from), Pointer(path)));

    /// <summary>Asserts that <paramref name="path"/> equals <paramref name="value"/>; the whole patch fails if it does not.</summary>
    public JsonPatchDocument Test(string path, JsonNode? value) => this.Append(() => JsonPatchOperation.Test(Pointer(path), value));

    JsonPatchDocument Append(Func<JsonPatchOperation> build)
    {
        try
        {
            this.operations.Add(build());
        }
        catch (JsonPatchException ex)
        {
            // Re-thrown with the position the operation would have had, so a builder error reads the same as
            // a parse error for the same mistake.
            throw new JsonPatchException(ex.Kind, ex.Message, this.operations.Count, ex.Operation);
        }
        return this;
    }

    static JsonPointer Pointer(string pointer)
        => JsonPointer.TryParse(pointer, out var parsed, out var error)
            ? parsed
            : throw new JsonPatchException(JsonPatchErrorKind.Malformed, error);

    // ── Parsing ─────────────────────────────────────────────────────────

    /// <summary>Parses a patch document, throwing <see cref="JsonPatchException"/> (Malformed) when it is not valid RFC 6902.</summary>
    public static JsonPatchDocument Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Parse(Encoding.UTF8.GetBytes(json));
    }

    /// <inheritdoc cref="Parse(string)"/>
    public static JsonPatchDocument Parse(ReadOnlySpan<byte> utf8Json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(utf8Json, documentOptions: DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw NotJson(ex);
        }

        return FromNode(node);
    }

    /// <inheritdoc cref="Parse(string)"/>
    public static async Task<JsonPatchDocument> ParseAsync(Stream utf8Json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);

        JsonNode? node;
        try
        {
            node = await JsonNode
                .ParseAsync(utf8Json, documentOptions: DocumentOptions, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw NotJson(ex);
        }

        return FromNode(node);
    }

    // Duplicate members are refused: {"op":"add","op":"remove"} has no one right reading, and the two
    // readings disagree about what gets written.
    static readonly JsonDocumentOptions DocumentOptions = new() { AllowDuplicateProperties = false };

    static JsonPatchException NotJson(JsonException ex)
        => new(JsonPatchErrorKind.Malformed, $"The JSON Patch body is not valid JSON: {ex.Message}", -1, null, ex);

    /// <summary>Builds a patch from an already-parsed JSON array of operation objects.</summary>
    public static JsonPatchDocument FromNode(JsonNode? node)
    {
        if (node is not JsonArray array)
            throw new JsonPatchException(
                JsonPatchErrorKind.Malformed,
                "A JSON Patch document must be a JSON array of operation objects."
            );

        var operations = new List<JsonPatchOperation>(array.Count);

        for (var i = 0; i < array.Count; i++)
            operations.Add(ParseOperation(array[i], i));

        return new JsonPatchDocument(operations);
    }

    static JsonPatchOperation ParseOperation(JsonNode? node, int index)
    {
        if (node is not JsonObject obj)
            throw Malformed(index, "is not a JSON object");

        // Members an op does not define are ignored, as RFC 6902 §4 requires — a "value" on a remove, or an
        // extension member some client adds, is not an error.
        var opName = ReadString(obj, "op", index);
        if (!JsonPatchOperation.TryParseName(opName, out var op))
            throw Malformed(index, $"has an unknown op '{opName}'. Expected add, remove, replace, move, copy or test");

        var path = ReadPointer(obj, "path", index);

        try
        {
            switch (op)
            {
                case JsonPatchOperationType.Add:
                case JsonPatchOperationType.Replace:
                case JsonPatchOperationType.Test:
                    // "value": null is a value (JSON null). A missing "value" is not — TryGetPropertyValue is what
                    // tells the two apart; the indexer would report both as null.
                    if (!obj.TryGetPropertyValue("value", out var value))
                        throw Malformed(index, $"('{opName}') is missing the required 'value' member");

                    return op switch
                    {
                        JsonPatchOperationType.Add => JsonPatchOperation.Add(path, value),
                        JsonPatchOperationType.Replace => JsonPatchOperation.Replace(path, value),
                        _ => JsonPatchOperation.Test(path, value)
                    };

                case JsonPatchOperationType.Remove:
                    return JsonPatchOperation.Remove(path);

                case JsonPatchOperationType.Move:
                    return JsonPatchOperation.Move(ReadPointer(obj, "from", index), path);

                default:
                    return JsonPatchOperation.Copy(ReadPointer(obj, "from", index), path);
            }
        }
        catch (JsonPatchException ex) when (ex.OperationIndex < 0)
        {
            throw new JsonPatchException(ex.Kind, $"Operation {index}: {ex.Message}", index, ex.Operation);
        }
    }

    static string ReadString(JsonObject obj, string member, int index)
    {
        if (!obj.TryGetPropertyValue(member, out var node) || node is null)
            throw Malformed(index, $"is missing the required '{member}' member");

        if (node.GetValueKind() != JsonValueKind.String)
            throw Malformed(index, $"has a '{member}' that is not a string");

        return node.GetValue<string>();
    }

    static JsonPointer ReadPointer(JsonObject obj, string member, int index)
        => JsonPointer.TryParse(ReadString(obj, member, index), out var pointer, out var error)
            ? pointer
            : throw Malformed(index, $"has an invalid '{member}': {error}");

    static JsonPatchException Malformed(int index, string what)
        => new(JsonPatchErrorKind.Malformed, $"Operation {index} {what}.", index);

    // ── Applying ────────────────────────────────────────────────────────

    /// <summary>
    /// Applies the patch to a copy of <paramref name="document"/> and returns the copy.
    /// <para>
    /// <paramref name="document"/> is never modified, so a patch that fails at operation 5 has not
    /// half-applied operations 0–4 to anything — the caller's document is exactly as it was. The return value
    /// can be a different node kind altogether when an operation targets the root (<c>"path": ""</c>).
    /// </para>
    /// </summary>
    /// <exception cref="JsonPatchException">A location did not exist (409) or a <c>test</c> failed (409).</exception>
    public JsonNode? ApplyTo(JsonNode? document)
    {
        var root = document?.DeepClone();

        for (var i = 0; i < this.operations.Count; i++)
            root = Apply(root, this.operations[i], i);

        return root;
    }

    /// <summary>
    /// Applies the patch to a typed value by round-tripping it through JSON with <paramref name="typeInfo"/> —
    /// so member names are the ones the client sees (naming policy and all), and nothing is reflected over.
    /// <para>
    /// Returns a new instance; <paramref name="value"/> is not modified. A result that no longer deserializes
    /// as <typeparamref name="T"/> (a string where a number belongs, the object replaced by an array) is a
    /// <see cref="JsonPatchErrorKind.InvalidResult"/> — 422.
    /// </para>
    /// </summary>
    public T ApplyTo<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        var node = JsonSerializer.SerializeToNode(value, typeInfo);
        var patched = this.ApplyTo(node);

        try
        {
            var result = patched is null ? default : patched.Deserialize(typeInfo);

            return result ?? throw new JsonPatchException(
                JsonPatchErrorKind.InvalidResult,
                $"The patched document is null, which is not a valid {typeof(T).Name}."
            );
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new JsonPatchException(
                JsonPatchErrorKind.InvalidResult,
                $"The patched document is not a valid {typeof(T).Name}: {ex.Message}",
                -1,
                null,
                ex
            );
        }
    }

    // Deliberately no ApplyTo<T>(T) that pulls metadata from JsonTypeInfoRegistry: C# would prefer it over
    // ApplyTo(JsonNode?) for a JsonObject argument (an exact generic match beats a conversion), silently
    // turning a node patch into a registry lookup for JsonObject. Naming the JsonTypeInfo keeps the two apart.

    static JsonNode? Apply(JsonNode? root, JsonPatchOperation operation, int index)
    {
        switch (operation.Op)
        {
            case JsonPatchOperationType.Add:
                return Add(root, operation.Path, operation.Value, operation, index);

            case JsonPatchOperationType.Remove:
                Remove(root, operation.Path, operation, index);
                return root;

            case JsonPatchOperationType.Replace:
                // Replace is remove-then-add with one extra guarantee: the target must already exist. For an
                // array that matters — remove-then-add would shift everything after it and back again, while
                // replacing in place is the same result without the churn.
                if (!operation.Path.TryEvaluate(root, out _))
                    throw NotFound(operation, index, operation.Path);

                if (operation.Path.IsRoot)
                    return operation.Value;

                var (container, key) = Container(root, operation.Path, operation, index);
                if (container is JsonObject obj)
                    obj[key] = operation.Value;
                else
                    ((JsonArray)container)[ExistingIndex((JsonArray)container, key, operation, index)] = operation.Value;

                return root;

            case JsonPatchOperationType.Move:
            {
                var from = operation.From!;
                if (!from.TryEvaluate(root, out _))
                    throw NotFound(operation, index, from);

                // Moving a value onto itself is a no-op that still had to find its source.
                if (from.Equals(operation.Path))
                    return root;

                var moved = Remove(root, from, operation, index);
                return Add(root, operation.Path, moved, operation, index);
            }

            case JsonPatchOperationType.Copy:
            {
                var from = operation.From!;
                if (!from.TryEvaluate(root, out var source))
                    throw NotFound(operation, index, from);

                return Add(root, operation.Path, source?.DeepClone(), operation, index);
            }

            default:
            {
                if (!operation.Path.TryEvaluate(root, out var actual))
                    throw NotFound(operation, index, operation.Path);

                if (!JsonPatchEquality.DeepEquals(actual, operation.RawValue))
                    throw new JsonPatchException(
                        JsonPatchErrorKind.TestFailed,
                        $"Operation {index} ('test' at '{operation.Path}') failed: the value is "
                            + $"{Describe(actual)}, not {Describe(operation.RawValue)}.",
                        index,
                        operation
                    );

                return root;
            }
        }
    }

    static JsonNode? Add(JsonNode? root, JsonPointer path, JsonNode? value, JsonPatchOperation operation, int index)
    {
        // "path": "" replaces the whole document — RFC 6902 §4.1 says add at the root is a replace.
        if (path.IsRoot)
            return value;

        var (container, key) = Container(root, path, operation, index);

        if (container is JsonObject obj)
        {
            // Adding to an existing member replaces it (§4.1): an add is "make this be here", not "insert or fail".
            obj[key] = value;
            return root;
        }

        var array = (JsonArray)container;

        if (key == "-")
        {
            array.Add(value);
            return root;
        }

        // An index may equal Count — that is "insert at the end" — but not exceed it: §4.1 makes an index
        // past the end an error rather than padding with nulls.
        if (!JsonPointer.TryParseArrayIndex(key, out var position) || position > array.Count)
            throw NotFound(operation, index, path);

        array.Insert(position, value);
        return root;
    }

    static JsonNode? Remove(JsonNode? root, JsonPointer path, JsonPatchOperation operation, int index)
    {
        var (container, key) = Container(root, path, operation, index);

        if (container is JsonObject obj)
        {
            if (!obj.Remove(key, out var removed))
                throw NotFound(operation, index, path);

            return removed;
        }

        var array = (JsonArray)container;
        var position = ExistingIndex(array, key, operation, index, path);
        var element = array[position];
        array.RemoveAt(position);

        return element;
    }

    /// <summary>The object or array that holds the last segment of <paramref name="path"/>, and that segment.</summary>
    static (JsonNode Container, string Key) Container(JsonNode? root, JsonPointer path, JsonPatchOperation operation, int index)
    {
        if (!path.Parent!.TryEvaluate(root, out var parent) || parent is not (JsonObject or JsonArray))
            throw NotFound(operation, index, path);

        return (parent, path.LastSegment!);
    }

    /// <summary>An index that must name an existing element — so neither <c>-</c> nor <c>Count</c> is allowed.</summary>
    static int ExistingIndex(JsonArray array, string key, JsonPatchOperation operation, int index, JsonPointer? path = null)
        => JsonPointer.TryParseArrayIndex(key, out var position) && position < array.Count
            ? position
            : throw NotFound(operation, index, path ?? operation.Path);

    static JsonPatchException NotFound(JsonPatchOperation operation, int index, JsonPointer path)
        => new(
            JsonPatchErrorKind.PathNotFound,
            $"Operation {index} ('{operation.OpName}') failed: the location '{path}' does not exist in the document.",
            index,
            operation
        );

    static string Describe(JsonNode? node)
    {
        var json = node?.ToJsonString() ?? "null";
        return json.Length <= 100 ? json : json[..100] + "…";
    }

    // ── Writing ─────────────────────────────────────────────────────────

    /// <summary>Writes the patch as its RFC 6902 JSON array.</summary>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartArray();
        foreach (var operation in this.operations)
            operation.WriteTo(writer);

        writer.WriteEndArray();
    }

    /// <summary>The patch as compact JSON, ready to send as an <c>application/json-patch+json</c> body.</summary>
    public string ToJsonString()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            this.WriteTo(writer);

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public override string ToString() => this.ToJsonString();
}

/// <summary>
/// Lets a <see cref="JsonPatchDocument"/> appear in an app's own <c>JsonSerializerContext</c> — as a
/// property of a larger request, say — without that context needing to know its shape. Attached with
/// <see cref="JsonConverterAttribute"/>, which the source generator honours without reflection.
/// </summary>
public sealed class JsonPatchDocumentConverter : JsonConverter<JsonPatchDocument>
{
    public override JsonPatchDocument? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        try
        {
            return JsonPatchDocument.FromNode(JsonNode.Parse(ref reader));
        }
        catch (JsonPatchException ex)
        {
            // The serializer's contract is JsonException for bad input; the patch's own message rides along.
            throw new JsonException(ex.Message, ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, JsonPatchDocument value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.WriteTo(writer);
    }
}

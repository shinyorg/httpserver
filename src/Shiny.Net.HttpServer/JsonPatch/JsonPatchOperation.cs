using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiny.Net.HttpServer.JsonPatch;

/// <summary>The six RFC 6902 operations.</summary>
public enum JsonPatchOperationType
{
    Add,
    Remove,
    Replace,
    Move,
    Copy,
    Test
}

/// <summary>
/// One operation in a <see cref="JsonPatchDocument"/>.
/// <para>
/// Immutable, and validated when it is built: an operation that exists is one RFC 6902 allows —
/// <c>from</c> present exactly where the op needs it, <c>value</c> present (possibly as JSON
/// <c>null</c>) exactly where the op needs it, and never a <c>move</c> into its own subtree. That
/// leaves applying it only the failures that depend on the document, which is what separates a 400
/// (the patch is wrong) from a 409 (the patch does not fit this resource).
/// </para>
/// </summary>
public sealed class JsonPatchOperation
{
    readonly JsonNode? value;

    JsonPatchOperation(JsonPatchOperationType op, JsonPointer path, JsonPointer? from, JsonNode? value)
    {
        this.Op = op;
        this.Path = path;
        this.From = from;
        this.value = value;
    }

    public JsonPatchOperationType Op { get; }

    /// <summary>The <c>op</c> member as it appears on the wire — <c>add</c>, <c>test</c>, ….</summary>
    public string OpName => Name(this.Op);

    /// <summary>The target location.</summary>
    public JsonPointer Path { get; }

    /// <summary>The source location of a <c>move</c> or <c>copy</c>; null for every other op.</summary>
    public JsonPointer? From { get; }

    /// <summary>True for the ops that carry a <c>value</c>: add, replace and test.</summary>
    public bool HasValue => this.Op is JsonPatchOperationType.Add or JsonPatchOperationType.Replace or JsonPatchOperationType.Test;

    /// <summary>
    /// The operation's <c>value</c> — a fresh copy on every read, so applying the same document to two
    /// resources can never leave them sharing (or fighting over the parent of) one node. JSON <c>null</c>
    /// is a legal value and comes back as null; check <see cref="HasValue"/> to tell it from "no value".
    /// </summary>
    public JsonNode? Value => this.value?.DeepClone();

    internal JsonNode? RawValue => this.value;

    public static JsonPatchOperation Add(JsonPointer path, JsonNode? value)
        => new(JsonPatchOperationType.Add, Require(path), null, Detach(value));

    public static JsonPatchOperation Remove(JsonPointer path)
    {
        // Removing the whole document leaves nothing to be a resource. RFC 6902 does not define it and
        // implementations disagree, so it is refused rather than guessed at — replace "" instead.
        if (Require(path).IsRoot)
            throw new JsonPatchException(
                JsonPatchErrorKind.Malformed,
                "'remove' cannot target the whole document (path \"\"). Use 'replace' to swap the document out."
            );

        return new(JsonPatchOperationType.Remove, path, null, null);
    }

    public static JsonPatchOperation Replace(JsonPointer path, JsonNode? value)
        => new(JsonPatchOperationType.Replace, Require(path), null, Detach(value));

    public static JsonPatchOperation Move(JsonPointer from, JsonPointer path)
    {
        ArgumentNullException.ThrowIfNull(from);

        // RFC 6902 §4.4: a value cannot be moved into one of its own children — there would be nowhere
        // left for the child to live once its parent was lifted out.
        if (from.IsProperPrefixOf(Require(path)))
            throw new JsonPatchException(
                JsonPatchErrorKind.Malformed,
                $"'move' cannot move '{from}' into its own child '{path}'."
            );

        return new(JsonPatchOperationType.Move, path, from, null);
    }

    public static JsonPatchOperation Copy(JsonPointer from, JsonPointer path)
    {
        ArgumentNullException.ThrowIfNull(from);
        return new(JsonPatchOperationType.Copy, Require(path), from, null);
    }

    public static JsonPatchOperation Test(JsonPointer path, JsonNode? value)
        => new(JsonPatchOperationType.Test, Require(path), null, Detach(value));

    internal static string Name(JsonPatchOperationType op) => op switch
    {
        JsonPatchOperationType.Add => "add",
        JsonPatchOperationType.Remove => "remove",
        JsonPatchOperationType.Replace => "replace",
        JsonPatchOperationType.Move => "move",
        JsonPatchOperationType.Copy => "copy",
        _ => "test"
    };

    internal static bool TryParseName(string? name, out JsonPatchOperationType op)
    {
        // Case-sensitive on purpose. RFC 6902 names the ops in lower case, and "Add" or "REMOVE" is a
        // patch some other tool wrote to a different spec — better refused than half-understood.
        switch (name)
        {
            case "add": op = JsonPatchOperationType.Add; return true;
            case "remove": op = JsonPatchOperationType.Remove; return true;
            case "replace": op = JsonPatchOperationType.Replace; return true;
            case "move": op = JsonPatchOperationType.Move; return true;
            case "copy": op = JsonPatchOperationType.Copy; return true;
            case "test": op = JsonPatchOperationType.Test; return true;
            default: op = default; return false;
        }
    }

    /// <summary>Writes the operation as its RFC 6902 JSON object.</summary>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteString("op", this.OpName);

        if (this.From is { } from)
            writer.WriteString("from", from.ToString());

        writer.WriteString("path", this.Path.ToString());

        if (this.HasValue)
        {
            writer.WritePropertyName("value");
            if (this.value is null)
                writer.WriteNullValue();
            else
                this.value.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    /// <summary>The operation as compact JSON, e.g. <c>{"op":"remove","path":"/a"}</c>.</summary>
    public override string ToString()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            this.WriteTo(writer);

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    static JsonPointer Require(JsonPointer path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path;
    }

    // A node that already belongs to a tree cannot be adopted by another; cloning also means a caller who
    // keeps mutating the node they passed in cannot change an operation after it was built.
    static JsonNode? Detach(JsonNode? value) => value?.DeepClone();
}

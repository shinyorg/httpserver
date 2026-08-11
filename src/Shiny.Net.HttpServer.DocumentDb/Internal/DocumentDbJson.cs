using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Shiny.Net.HttpServer.DocumentDb.Internal;

/// <summary>
/// Metadata for the JSON shapes these endpoints handle themselves.
/// <para>
/// The documents are the caller's problem — their metadata comes from
/// <c>DocumentEndpointOptions&lt;T&gt;.TypeInfo</c> or the store. These are the envelopes and raw bodies the
/// endpoints read and write on their own, and they are declared here so no path falls back to the reflection
/// serializer.
/// </para>
/// </summary>
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(JsonNode))]
internal partial class DocumentDbJson : JsonSerializerContext;

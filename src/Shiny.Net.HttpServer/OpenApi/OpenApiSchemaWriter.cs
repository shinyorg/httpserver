using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Shiny.Net.HttpServer.OpenApi;

/// <summary>
/// Turns CLR types into OpenAPI schemas without touching reflection.
/// <para>
/// The shape of a type is already described, precisely and at compile time, by the
/// <see cref="JsonTypeInfo"/> the app's <c>JsonSerializerContext</c> generated for it — property
/// names after naming policy, property types, what is required. Reading the schema off that instead
/// of off <c>Type.GetProperties()</c> means the document is generated from the same metadata that
/// does the serializing, so it cannot describe a payload the server would not actually produce, and
/// it survives trimming and AOT untouched.
/// </para>
/// <para>
/// The cost is that a type must be registered with <see cref="JsonTypeInfoRegistry"/> to be
/// described. That is the same requirement as returning it from an endpoint, and the generator
/// warns (SWS006) when it is not met.
/// </para>
/// </summary>
sealed class OpenApiSchemaWriter
{
    readonly Dictionary<Type, string> componentNames = [];
    readonly HashSet<string> usedNames = [];
    readonly Queue<Type> pending = new();
    readonly HashSet<Type> queued = [];

    /// <summary>Writes a schema for <paramref name="type"/>, inline or as a <c>$ref</c>.</summary>
    public void WriteSchema(Utf8JsonWriter writer, Type type)
    {
        writer.WriteStartObject();
        this.WriteSchemaBody(writer, type, nullable: false);
        writer.WriteEndObject();
    }

    void WriteSchemaBody(Utf8JsonWriter writer, Type type, bool nullable)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            this.WriteSchemaBody(writer, underlying, nullable: true);
            return;
        }

        if (TryWritePrimitive(writer, type, nullable))
            return;

        if (type.IsEnum)
        {
            WriteEnum(writer, type, nullable);
            return;
        }

        if (type.IsArray && type.GetElementType() is { } element)
        {
            this.WriteArray(writer, element, nullable);
            return;
        }

        if (JsonTypeInfoRegistry.TryGet(type, out var typeInfo))
        {
            switch (typeInfo.Kind)
            {
                case JsonTypeInfoKind.Enumerable when typeInfo.ElementType is { } item:
                    this.WriteArray(writer, item, nullable);
                    return;

                case JsonTypeInfoKind.Dictionary when typeInfo.ElementType is { } value:
                    writer.WriteString("type", "object");
                    if (nullable)
                        writer.WriteBoolean("nullable", true);

                    writer.WritePropertyName("additionalProperties");
                    this.WriteSchema(writer, value);
                    return;

                case JsonTypeInfoKind.Object:
                    this.WriteReference(writer, type, nullable);
                    return;
            }
        }

        // Nothing known about it. An empty schema means "any", which is honest — better than
        // inventing a shape the server never promised.
        if (nullable)
            writer.WriteBoolean("nullable", true);
    }

    void WriteArray(Utf8JsonWriter writer, Type elementType, bool nullable)
    {
        writer.WriteString("type", "array");
        if (nullable)
            writer.WriteBoolean("nullable", true);

        writer.WritePropertyName("items");
        this.WriteSchema(writer, elementType);
    }

    void WriteReference(Utf8JsonWriter writer, Type type, bool nullable)
    {
        var name = this.EnsureComponent(type);

        // OpenAPI 3.0 ignores sibling keywords next to $ref, so a nullable reference has to be
        // wrapped. 3.1 fixed this; 3.0 is what most tooling still reads.
        if (nullable)
        {
            writer.WriteBoolean("nullable", true);
            writer.WriteStartArray("allOf");
            writer.WriteStartObject();
            writer.WriteString("$ref", "#/components/schemas/" + name);
            writer.WriteEndObject();
            writer.WriteEndArray();
            return;
        }

        writer.WriteString("$ref", "#/components/schemas/" + name);
    }

    string EnsureComponent(Type type)
    {
        if (this.componentNames.TryGetValue(type, out var existing))
            return existing;

        var name = SanitizeName(type);
        if (!this.usedNames.Add(name))
        {
            // Two types with the same short name in different namespaces. Qualify rather than
            // silently merge them into one schema.
            var qualified = SanitizeName(type, includeNamespace: true);
            var suffix = 2;
            while (!this.usedNames.Add(qualified))
                qualified = SanitizeName(type, includeNamespace: true) + suffix++;

            name = qualified;
        }

        this.componentNames[type] = name;

        if (this.queued.Add(type))
            this.pending.Enqueue(type);

        return name;
    }

    /// <summary>True when anything referenced a component schema.</summary>
    public bool HasComponents => this.pending.Count > 0;

    /// <summary>
    /// Writes the <c>schemas</c> block, including schemas discovered while writing earlier ones. The
    /// caller owns the surrounding <c>components</c> object, because security schemes live there too.
    /// </summary>
    public void WriteSchemas(Utf8JsonWriter writer)
    {
        if (this.pending.Count == 0)
            return;

        writer.WriteStartObject("schemas");

        // Writing one object can reference another, which enqueues it — hence a queue rather than a
        // snapshot of what was known when we started.
        while (this.pending.Count > 0)
        {
            var type = this.pending.Dequeue();
            writer.WritePropertyName(this.componentNames[type]);
            this.WriteObjectSchema(writer, type);
        }

        writer.WriteEndObject();
    }

    void WriteObjectSchema(Utf8JsonWriter writer, Type type)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "object");

        if (!JsonTypeInfoRegistry.TryGet(type, out var typeInfo) || typeInfo.Properties.Count == 0)
        {
            writer.WriteEndObject();
            return;
        }

        writer.WriteStartObject("properties");
        foreach (var property in typeInfo.Properties)
        {
            if (property.IsExtensionData)
                continue;

            writer.WritePropertyName(property.Name);
            this.WriteSchema(writer, property.PropertyType);
        }
        writer.WriteEndObject();

        var required = typeInfo.Properties
            .Where(p => p is { IsRequired: true, IsExtensionData: false })
            .Select(p => p.Name)
            .ToArray();

        if (required.Length > 0)
        {
            writer.WriteStartArray("required");
            foreach (var name in required)
                writer.WriteStringValue(name);
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    static void WriteEnum(Utf8JsonWriter writer, Type type, bool nullable)
    {
        // Serialized as names, matching JsonStringEnumConverter, which is how an enum crossing this
        // boundary is normally configured. A numeric enum still validates as a string in the doc
        // only if the app said so, so the names are the safer default to publish.
        writer.WriteString("type", "string");
        if (nullable)
            writer.WriteBoolean("nullable", true);

        writer.WriteStartArray("enum");
        foreach (var name in Enum.GetNames(type))
            writer.WriteStringValue(name);
        writer.WriteEndArray();
    }

    static bool TryWritePrimitive(Utf8JsonWriter writer, Type type, bool nullable)
    {
        string? format = null;
        string schemaType;

        if (type == typeof(string))
        {
            schemaType = "string";
        }
        else if (type == typeof(bool))
        {
            schemaType = "boolean";
        }
        else if (type == typeof(byte) || type == typeof(sbyte) ||
                 type == typeof(short) || type == typeof(ushort) ||
                 type == typeof(int) || type == typeof(uint))
        {
            schemaType = "integer";
            format = "int32";
        }
        else if (type == typeof(long) || type == typeof(ulong))
        {
            schemaType = "integer";
            format = "int64";
        }
        else if (type == typeof(float))
        {
            schemaType = "number";
            format = "float";
        }
        else if (type == typeof(double) || type == typeof(decimal))
        {
            schemaType = "number";
            format = "double";
        }
        else if (type == typeof(Guid))
        {
            schemaType = "string";
            format = "uuid";
        }
        else if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            schemaType = "string";
            format = "date-time";
        }
        else if (type == typeof(DateOnly))
        {
            schemaType = "string";
            format = "date";
        }
        else if (type == typeof(TimeOnly))
        {
            schemaType = "string";
            format = "time";
        }
        else if (type == typeof(TimeSpan))
        {
            schemaType = "string";
            format = "duration";
        }
        else if (type == typeof(Uri))
        {
            schemaType = "string";
            format = "uri";
        }
        else if (type == typeof(char))
        {
            schemaType = "string";
        }
        else
        {
            return false;
        }

        writer.WriteString("type", schemaType);
        if (format is not null)
            writer.WriteString("format", format);

        if (nullable)
            writer.WriteBoolean("nullable", true);

        return true;
    }

    static string SanitizeName(Type type, bool includeNamespace = false)
    {
        var name = includeNamespace && type.Namespace is { Length: > 0 } ns
            ? ns + "." + type.Name
            : type.Name;

        // Generic arity ("Page`1") and namespace dots are not valid in a component key.
        var backtick = name.IndexOf('`');
        if (backtick >= 0)
            name = name[..backtick];

        return name.Replace('.', '_').Replace('+', '_');
    }
}

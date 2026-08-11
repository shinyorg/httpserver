namespace Shiny.Net.HttpServer.Mcp;

/// <summary>
/// Turns the one MCP failure trimming introduces into something the caller can act on.
/// <para>
/// A tool whose parameters or results are anything more than primitives has to describe them to the
/// client as a JSON schema, and building that schema by reflection does not survive trimming. The
/// call site says nothing about this: it compiles clean, publishes clean, and then <c>MapMcp()</c>
/// throws <see cref="NotSupportedException"/> from somewhere twenty frames deep in the container,
/// naming a type and offering no hint that a <c>JsonSerializerContext</c> is what was wanted.
/// </para>
/// <para>
/// The timing was never the problem — the MCP server graph is built when the endpoint is mapped,
/// which is early enough. Only the message was.
/// </para>
/// </summary>
static class McpStartupValidation
{
    public static T Guarded<T>(Func<T> resolve)
    {
        try
        {
            return resolve();
        }
        catch (NotSupportedException ex) when (IsMissingJsonMetadata(ex))
        {
            throw new InvalidOperationException(MissingJsonMetadataMessage(ex), ex);
        }

        // Anything else propagates untouched. Dressing an unrelated fault up in advice about JSON
        // contexts would send the caller somewhere there is nothing to find.
    }

    /// <summary>
    /// Whether this is System.Text.Json reporting that a type is absent from the resolver chain.
    /// <para>
    /// Matched on the message, because the exception carries the distinction nowhere else. Getting
    /// it wrong costs only the better message — an unrecognised fault still reaches the caller as
    /// itself, from the same line, at the same time.
    /// </para>
    /// </summary>
    static bool IsMissingJsonMetadata(Exception ex)
        => ex.Message.Contains("JsonTypeInfo", StringComparison.Ordinal);

    static string MissingJsonMetadataMessage(Exception ex) =>
        $"""
        The MCP server could not describe one of its tools, prompts or resources.

        {ex.Message}

        The parameter and return types of a tool are published to the client as a JSON schema, and
        building that schema by reflection does not survive trimming — which covers .NET MAUI on iOS
        and Mac Catalyst, and anything published with PublishTrimmed or PublishAot. Hand the MCP
        server a source-generated context that covers those types:

            [JsonSerializable(typeof(Query))]
            [JsonSerializable(typeof(IReadOnlyList<Reading>))]
            public partial class ToolJson : JsonSerializerContext;

            services
                .AddMcpServer()
                .WithTools<MyTools>(ToolJson.Default.Options)
                .WithHttpTransport();

        Tools whose parameters and results are only primitives and strings are described without
        one and need no context.
        """;
}

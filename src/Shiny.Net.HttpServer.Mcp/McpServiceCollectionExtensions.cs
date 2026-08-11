using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.Net.HttpServer.Mcp;

/// <summary>
/// Registration. The MCP server itself — its tools, prompts and resources — is configured with the
/// SDK's own <c>AddMcpServer()</c>; what these add is the HTTP transport in front of it.
/// </summary>
public static class McpServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Streamable HTTP transport, mirroring the SDK's ASP.NET Core call of the same name so
    /// the registration reads identically on both hosts.
    /// <code>
    /// builder.Services
    ///     .AddMcpServer(o => o.ServerInfo = new() { Name = "thermostat", Version = "1.0.0" })
    ///     .WithTools&lt;ThermostatTools&gt;()
    ///     .WithHttpTransport(o => o.AllowedOrigins.Add("https://inspector.example.com"));
    ///
    /// var app = builder.Build();
    /// app.MapMcp();
    /// </code>
    /// </summary>
    public static IMcpServerBuilder WithHttpTransport(
        this IMcpServerBuilder builder,
        Action<McpHttpOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddMcpHttpTransport(configure);
        return builder;
    }

    /// <summary>
    /// Adds the Streamable HTTP transport without the builder in hand — for apps that configure the
    /// MCP server somewhere else entirely.
    /// </summary>
    public static IServiceCollection AddMcpHttpTransport(
        this IServiceCollection services,
        Action<McpHttpOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions();

        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton<McpHttpSessionManager>();
        services.TryAddSingleton<McpHttpHandler>();

        return services;
    }
}

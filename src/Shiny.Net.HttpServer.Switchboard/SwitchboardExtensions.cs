using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.Net.HttpServer.Switchboard.Internal;
using Shiny.Net.HttpServer.Timeouts;

namespace Shiny.Net.HttpServer.Switchboard;

/// <summary>Registering switchboards.</summary>
public static class SwitchboardServiceExtensions
{
    /// <summary>
    /// Registers the switchboard runtime, the options every switchboard shares, and an
    /// <see cref="IOperator{TBoard}"/> for every switchboard type.
    /// <code>
    /// builder.AddSwitchboard(o => o.ResumeWindow = TimeSpan.FromSeconds(60));
    /// </code>
    /// </summary>
    public static ShinyHttpServerBuilder AddSwitchboard(this ShinyHttpServerBuilder builder, Action<SwitchboardOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        OptionsRegistration.Configure(builder.Services, configure);

        builder.Services.TryAddSingleton<IUserIdProvider, DefaultUserIdProvider>();
        builder.Services.TryAddSingleton<SwitchboardRuntime>();

        // Open generic, closed over a reference type (every switchboard is a class), which the
        // container can build under Native AOT without generating code at run time.
        builder.Services.TryAdd(ServiceDescriptor.Singleton(typeof(IOperator<>), typeof(Operator<>)));
        builder.Services.TryAdd(ServiceDescriptor.Singleton(typeof(IOperator<,>), typeof(Operator<,>)));

        return builder;
    }
}

/// <summary>Registering switchboard filters.</summary>
public static class SwitchboardFilterExtensions
{
    /// <summary>
    /// Adds a filter that wraps every switchboard call and lifecycle callback, in registration order —
    /// the first added is outermost. Filters are singletons; reach scoped services through the
    /// context's <c>Services</c>.
    /// </summary>
    public static ShinyHttpServerBuilder AddSwitchboardFilter<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)] TFilter>(
        this ShinyHttpServerBuilder builder
    ) where TFilter : class, ISwitchboardFilter
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<ISwitchboardFilter, TFilter>();
        return builder;
    }

    /// <summary>Adds a filter instance. See <see cref="AddSwitchboardFilter{TFilter}(ShinyHttpServerBuilder)"/>.</summary>
    public static ShinyHttpServerBuilder AddSwitchboardFilter(this ShinyHttpServerBuilder builder, ISwitchboardFilter filter)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(filter);

        builder.Services.AddSingleton(filter);
        return builder;
    }
}

/// <summary>Mapping switchboards onto routes.</summary>
public static class SwitchboardEndpointExtensions
{
    /// <summary>
    /// Mounts a switchboard at <paramref name="path"/>: <c>GET {path}/connect</c> for the event
    /// stream, <c>POST {path}/invoke</c> for calls, <c>DELETE {path}/connect</c> to hang up, and
    /// <c>POST {path}/completion</c> for clients answering a server's question.
    /// <code>
    /// app.MapSwitchboard&lt;ChatBoard&gt;("/chat").RequireAuthorization();
    /// </code>
    /// </summary>
    public static SwitchboardEndpointBuilder MapSwitchboard<TBoard>(this HttpServer server, string path)
        where TBoard : Switchboard
    {
        ArgumentNullException.ThrowIfNull(server);

        return Map(
            typeof(TBoard),
            server.Services,
            path,
            (method, pattern, handler, metadata) => new RouteEndpointBuilder(server.MapRoute(method, pattern, handler, metadata))
        );
    }

    /// <summary>Mounts a switchboard inside a module or route group.</summary>
    public static SwitchboardEndpointBuilder MapSwitchboard<TBoard>(this IEndpointRouteBuilder endpoints, string path)
        where TBoard : Switchboard
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return Map(typeof(TBoard), endpoints.Services, path, endpoints.Map);
    }

    static SwitchboardEndpointBuilder Map(
        Type boardType,
        IServiceProvider? services,
        string path,
        Func<string, string, RequestDelegate, object[], RouteEndpointBuilder> map
    )
    {
        ArgumentNullException.ThrowIfNull(path);

        var runtime = services?.GetService<SwitchboardRuntime>()
            ?? throw new InvalidOperationException(
                "No switchboard runtime is registered. Call builder.AddSwitchboard() before mapping a switchboard."
            );

        var descriptor = SwitchboardRegistry.GetRequired(boardType);
        var board = runtime.GetBoard(descriptor);
        var root = path.Trim().TrimEnd('/');

        object[] Metadata() =>
        [
            .. descriptor.Metadata,

            // An event stream is open for as long as the line is, and a call can legitimately wait on a
            // long stream or on a client's answer — a blanket request timeout would cut both.
            new RequestTimeoutMetadata { Disabled = true }
        ];

        var endpoints = new[]
        {
            map(HttpMethods.Get, $"{root}/{SwitchboardProtocol.ConnectPath}", ctx => SwitchboardEndpoints.ConnectAsync(ctx, board), Metadata()),
            map(HttpMethods.Post, $"{root}/{SwitchboardProtocol.InvokePath}", ctx => SwitchboardEndpoints.InvokeAsync(ctx, board), Metadata()),
            map(HttpMethods.Delete, $"{root}/{SwitchboardProtocol.ConnectPath}", ctx => SwitchboardEndpoints.HangUpAsync(ctx, board), Metadata()),
            map(HttpMethods.Post, $"{root}/{SwitchboardProtocol.CompletionPath}", ctx => SwitchboardEndpoints.CompleteAsync(ctx, board), Metadata())
        };

        return new SwitchboardEndpointBuilder(endpoints);
    }
}

/// <summary>A mapped switchboard's routes, so conventions apply to all of them in one call.</summary>
public sealed class SwitchboardEndpointBuilder
{
    internal SwitchboardEndpointBuilder(IReadOnlyList<RouteEndpointBuilder> endpoints) => this.Endpoints = endpoints;

    /// <summary>The connect, invoke, hang-up and completion routes.</summary>
    public IReadOnlyList<RouteEndpointBuilder> Endpoints { get; }

    /// <summary>Requires authorization on every route, optionally against named policies.</summary>
    public SwitchboardEndpointBuilder RequireAuthorization(params string[] policies)
        => this.Apply(e => e.RequireAuthorization(policies));

    public SwitchboardEndpointBuilder RequireCors(string policyName)
        => this.Apply(e => e.RequireCors(policyName));

    public SwitchboardEndpointBuilder RequireRateLimiting(string policyName)
        => this.Apply(e => e.RequireRateLimiting(policyName));

    public SwitchboardEndpointBuilder RequireIpFilter(string policyName)
        => this.Apply(e => e.RequireIpFilter(policyName));

    public SwitchboardEndpointBuilder WithMetadata(object metadata)
        => this.Apply(e => e.WithMetadata(metadata));

    SwitchboardEndpointBuilder Apply(Action<RouteEndpointBuilder> convention)
    {
        foreach (var endpoint in this.Endpoints)
            convention(endpoint);

        return this;
    }
}

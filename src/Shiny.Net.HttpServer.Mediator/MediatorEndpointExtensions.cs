using Shiny.Mediator;
using Shiny.Net.HttpServer.Endpoints;

namespace Shiny.Net.HttpServer.Mediator;

/// <summary>
/// Maps mediator contracts to routes by hand, for the cases the attributes do not cover — a route
/// decided at runtime, a contract you do not own, or a handler in another assembly.
/// <para>
/// Prefer the attributes. These exist because a generator can only see what is declared in source,
/// and are the same calls the generated code makes.
/// </para>
/// <para>
/// The verb split is not stylistic. A body verb can read the whole contract out of the request body
/// with the registered JSON metadata, which needs nothing from the caller. GET and DELETE carry
/// their values in the route and query string, and turning those into a contract without reflection
/// means somebody has to write the assignment — the generator does it for you, or you pass a
/// <c>bind</c> delegate here.
/// </para>
/// </summary>
public static class MediatorEndpointExtensions
{
    // ---- Requests ----

    /// <summary>Maps a POST that reads the contract from the JSON body and returns its result.</summary>
    public static RouteEndpointBuilder MapMediatorPost<TRequest, TResult>(
        this IEndpointRouteBuilder endpoints,
        string pattern
    ) where TRequest : IRequest<TResult>
        => endpoints.MapBodyRequest<TRequest, TResult>(HttpMethods.Post, pattern);

    /// <summary>Maps a PUT that reads the contract from the JSON body and returns its result.</summary>
    public static RouteEndpointBuilder MapMediatorPut<TRequest, TResult>(
        this IEndpointRouteBuilder endpoints,
        string pattern
    ) where TRequest : IRequest<TResult>
        => endpoints.MapBodyRequest<TRequest, TResult>(HttpMethods.Put, pattern);

    /// <summary>Maps a PATCH that reads the contract from the JSON body and returns its result.</summary>
    public static RouteEndpointBuilder MapMediatorPatch<TRequest, TResult>(
        this IEndpointRouteBuilder endpoints,
        string pattern
    ) where TRequest : IRequest<TResult>
        => endpoints.MapBodyRequest<TRequest, TResult>(HttpMethods.Patch, pattern);

    /// <summary>
    /// Maps a GET, building the contract from the request with <paramref name="bind"/>.
    /// <code>
    /// endpoints.MapMediatorGet&lt;GetWidget, Widget&gt;(
    ///     "/api/widgets/{id}",
    ///     ctx => new GetWidget(int.Parse(ctx.Request.RouteValues["id"]!))
    /// );
    /// </code>
    /// </summary>
    public static RouteEndpointBuilder MapMediatorGet<TRequest, TResult>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<HttpContext, TRequest> bind
    ) where TRequest : IRequest<TResult>
        => endpoints.MapBoundRequest<TRequest, TResult>(HttpMethods.Get, pattern, bind);

    /// <summary>Maps a DELETE, building the contract from the request with <paramref name="bind"/>.</summary>
    public static RouteEndpointBuilder MapMediatorDelete<TRequest, TResult>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<HttpContext, TRequest> bind
    ) where TRequest : IRequest<TResult>
        => endpoints.MapBoundRequest<TRequest, TResult>(HttpMethods.Delete, pattern, bind);

    /// <summary>Maps a GET for a contract that carries nothing.</summary>
    public static RouteEndpointBuilder MapMediatorGet<TRequest, TResult>(
        this IEndpointRouteBuilder endpoints,
        string pattern
    ) where TRequest : IRequest<TResult>, new()
        => endpoints.MapBoundRequest<TRequest, TResult>(HttpMethods.Get, pattern, static _ => new TRequest());

    // ---- Commands ----

    /// <summary>Maps a POST that reads a command from the JSON body and answers with a status code.</summary>
    public static RouteEndpointBuilder MapMediatorPost<TCommand>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        int successStatusCode = StatusCodes.Status204NoContent
    ) where TCommand : ICommand
        => endpoints.MapBodyCommand<TCommand>(HttpMethods.Post, pattern, successStatusCode);

    /// <summary>Maps a PUT that reads a command from the JSON body and answers with a status code.</summary>
    public static RouteEndpointBuilder MapMediatorPut<TCommand>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        int successStatusCode = StatusCodes.Status204NoContent
    ) where TCommand : ICommand
        => endpoints.MapBodyCommand<TCommand>(HttpMethods.Put, pattern, successStatusCode);

    /// <summary>Maps a DELETE, building the command from the request with <paramref name="bind"/>.</summary>
    public static RouteEndpointBuilder MapMediatorDelete<TCommand>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<HttpContext, TCommand> bind,
        int successStatusCode = StatusCodes.Status204NoContent
    ) where TCommand : ICommand
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(bind);

        return endpoints.Map(
            HttpMethods.Delete,
            pattern,
            ctx => MediatorDispatch.SendAsync(ctx, bind(ctx), successStatusCode)
        );
    }

    // ---- Streams ----

    /// <summary>
    /// Maps a GET that returns a stream request as Server-Sent Events, building the contract with
    /// <paramref name="bind"/>.
    /// </summary>
    public static RouteEndpointBuilder MapMediatorServerSentEvents<TRequest, TResult>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<HttpContext, TRequest> bind,
        string? eventName = null
    ) where TRequest : IStreamRequest<TResult>
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(bind);

        return endpoints.Map(
            HttpMethods.Get,
            pattern,
            ctx => MediatorDispatch.StreamAsync<TResult>(ctx, bind(ctx), eventName)
        );
    }

    /// <summary>Maps a GET returning a stream request as SSE, for a contract that carries nothing.</summary>
    public static RouteEndpointBuilder MapMediatorServerSentEvents<TRequest, TResult>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        string? eventName = null
    ) where TRequest : IStreamRequest<TResult>, new()
        => endpoints.MapMediatorServerSentEvents<TRequest, TResult>(pattern, static _ => new TRequest(), eventName);

    // ---- The shared bodies ----

    static RouteEndpointBuilder MapBodyRequest<TRequest, TResult>(
        this IEndpointRouteBuilder endpoints,
        string method,
        string pattern
    ) where TRequest : IRequest<TResult>
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints.Map(method, pattern, async ctx =>
        {
            var body = await EndpointBinder
                .TryReadBodyAsync<TRequest>(ctx)
                .ConfigureAwait(false);

            if (!body.Success || body.Value is null)
            {
                await EndpointBinder
                    .BodyReadFailedAsync(ctx, "request", body.Status, typeof(TRequest).Name)
                    .ConfigureAwait(false);

                return;
            }

            await MediatorDispatch.RequestAsync<TResult>(ctx, body.Value).ConfigureAwait(false);
        });
    }

    static RouteEndpointBuilder MapBodyCommand<TCommand>(
        this IEndpointRouteBuilder endpoints,
        string method,
        string pattern,
        int successStatusCode
    ) where TCommand : ICommand
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints.Map(method, pattern, async ctx =>
        {
            var body = await EndpointBinder
                .TryReadBodyAsync<TCommand>(ctx)
                .ConfigureAwait(false);

            if (!body.Success || body.Value is null)
            {
                await EndpointBinder
                    .BodyReadFailedAsync(ctx, "command", body.Status, typeof(TCommand).Name)
                    .ConfigureAwait(false);

                return;
            }

            await MediatorDispatch.SendAsync(ctx, body.Value, successStatusCode).ConfigureAwait(false);
        });
    }

    static RouteEndpointBuilder MapBoundRequest<TRequest, TResult>(
        this IEndpointRouteBuilder endpoints,
        string method,
        string pattern,
        Func<HttpContext, TRequest> bind
    ) where TRequest : IRequest<TResult>
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(bind);

        return endpoints.Map(method, pattern, ctx => MediatorDispatch.RequestAsync<TResult>(ctx, bind(ctx)));
    }
}

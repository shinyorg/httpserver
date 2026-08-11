using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Mediator;

namespace Shiny.Net.HttpServer.Mediator;

/// <summary>
/// What a generated mediator endpoint calls once it has bound its contract.
/// <para>
/// The generator emits the binding and nothing else: every method here is closed over concrete
/// types at the call site, so the whole path stays free of reflection and survives trimming. It is
/// public because generated code lives in the user's assembly and has to be able to reach it — it
/// is not an API worth calling by hand.
/// </para>
/// </summary>
public static class MediatorDispatch
{
    /// <summary>
    /// Resolves the mediator from the request scope.
    /// <para>
    /// A missing registration is the single most likely mistake here — the attributes generate
    /// endpoints whether or not anyone called <c>AddShinyMediator</c>, and the failure would
    /// otherwise be a null reference on the first request.
    /// </para>
    /// </summary>
    public static IMediator Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.RequestServices.GetService<IMediator>()
            ?? throw new InvalidOperationException(
                "No IMediator is registered. A mediator endpoint needs Shiny.Mediator in the same "
                + "container as the server — call services.AddShinyMediator(...) during startup."
            );
    }

    /// <summary>Dispatches a request and writes its result as JSON.</summary>
    public static async ValueTask RequestAsync<TResult>(HttpContext context, IRequest<TResult> request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        var mediator = Resolve(context);
        var (_, result) = await mediator
            .Request(request, context.RequestAborted)
            .ConfigureAwait(false);

        await Results
            .Ok<TResult>(result)
            .ExecuteAsync(context)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a command and answers with a status code and no body.
    /// <para>
    /// 204 by default, because a command has nothing to return and a 200 with an empty body invites
    /// a client to go looking for one.
    /// </para>
    /// </summary>
    public static async ValueTask SendAsync<TCommand>(HttpContext context, TCommand command, int successStatusCode)
        where TCommand : ICommand
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(command);

        var mediator = Resolve(context);
        await mediator
            .Send(command, context.RequestAborted)
            .ConfigureAwait(false);

        await Results
            .StatusCode(successStatusCode)
            .ExecuteAsync(context)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Streams a mediator stream request as Server-Sent Events.
    /// <para>
    /// SSE rather than a JSON array because a stream request is open-ended: the caller wants each
    /// item as it arrives, and an array cannot be read until it closes. Each item is serialized
    /// through the registered <c>JsonSerializerContext</c>, so this stays reflection-free.
    /// </para>
    /// </summary>
    public static ValueTask StreamAsync<TResult>(
        HttpContext context,
        IStreamRequest<TResult> request,
        string? eventName = null
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        // Resolved before the response starts, so a missing registration is still a clean 500
        // rather than a half-written event stream.
        var mediator = Resolve(context);
        var typeInfo = JsonTypeInfoRegistry.GetRequired<TResult>();

        return Results
            .ServerSentEvents(async stream =>
            {
                var items = mediator.Request(request, stream.Aborted);

                await foreach (var (_, item) in items.ConfigureAwait(false))
                {
                    var json = JsonSerializer.Serialize(item, typeInfo);

                    if (eventName is { Length: > 0 })
                        await stream.SendAsync(eventName, json, stream.Aborted).ConfigureAwait(false);
                    else
                        await stream.SendAsync(json, stream.Aborted).ConfigureAwait(false);
                }
            })
            .ExecuteAsync(context);
    }
}

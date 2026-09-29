using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Net.HttpServer.Security;
using Shiny.Net.HttpServer.Sse;

namespace Shiny.Net.HttpServer.Switchboard.Internal;

/// <summary>The three routes a mapped switchboard answers.</summary>
static class SwitchboardEndpoints
{
    // ---- GET {base}/connect ----

    public static async ValueTask ConnectAsync(HttpContext context, BoardRuntime board)
    {
        // The header is the channel to prefer — a query string ends up in access logs, and the token
        // is a credential. The query form exists for clients that cannot set headers on a stream.
        var token = context.Request.Headers.GetFirst(SwitchboardProtocol.LineHeader)
            ?? context.Request.Query.GetFirst(SwitchboardProtocol.TokenQuery);

        if (token is { Length: > 0 })
        {
            await ResumeAsync(context, board, token).ConfigureAwait(false);
            return;
        }

        if (!await SwitchboardAuthorization.AuthorizeAsync(context, RouteAuthorization(context, board)).ConfigureAwait(false))
            return;

        var line = board.Open(context);
        if (line is null)
        {
            await SwitchboardResponses.ErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "Too many open lines").ConfigureAwait(false);
            return;
        }

        var attachment = line.Attach(null, context.Request.Protocol, context.Connection.RemoteIpAddress)!;
        var stream = await StartStreamAsync(context, board, line).ConfigureAwait(false);
        if (stream is null)
            return;

        var connected = JsonSerializer.Serialize(
            new ConnectedPayload(
                SwitchboardProtocol.Version,
                line.Id,
                line.Token,
                (long)board.Options.HeartbeatInterval.TotalMilliseconds,
                (long)board.Options.ResumeWindow.TotalMilliseconds
            ),
            SwitchboardProtocolJson.Default.ConnectedPayload
        );

        if (!await TrySendAsync(stream, SwitchboardProtocol.ConnectedEvent, connected, context.RequestAborted).ConfigureAwait(false))
        {
            await EndStreamAsync(context, board, line, attachment, stream).ConfigureAwait(false);
            return;
        }

        // The pump starts before OnConnectedAsync runs, so anything it sends — Clients.Caller first
        // of all — is delivered as it is sent rather than piling up behind the callback.
        var pump = PumpAsync(stream, attachment.Reader, board.Options.HeartbeatInterval, context.RequestAborted);

        try
        {
            await board.RunLifecycleAsync(line, context, context.RequestServices, Lifecycle.Connected).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger(context).LogError(ex, "OnConnectedAsync failed for line {LineId}; closing it", line.Id);
            board.Close(line, DisconnectReason.Faulted, "The server could not open the line", allowReconnect: false, ex);
        }

        await pump.ConfigureAwait(false);
        await EndStreamAsync(context, board, line, attachment, stream).ConfigureAwait(false);
    }

    static async ValueTask ResumeAsync(HttpContext context, BoardRuntime board, string token)
    {
        var line = board.FindByToken(token);
        if (line is null)
        {
            await Gone(context).ConfigureAwait(false);
            return;
        }

        if (!await IsSameCallerAsync(context, line).ConfigureAwait(false))
            return;

        var lastEventId = ParseLastEventId(context);
        var attachment = line.Attach(lastEventId, context.Request.Protocol, context.Connection.RemoteIpAddress);
        if (attachment is null)
        {
            await Gone(context).ConfigureAwait(false);
            return;
        }

        var stream = await StartStreamAsync(context, board, line).ConfigureAwait(false);
        if (stream is null)
            return;

        SwitchboardMetrics.LineResumed(board.Name, attachment.Missed);
        var resumed = JsonSerializer.Serialize(new ResumedPayload(line.Id, attachment.Missed), SwitchboardProtocolJson.Default.ResumedPayload);

        if (!await TrySendAsync(stream, SwitchboardProtocol.ResumedEvent, resumed, context.RequestAborted).ConfigureAwait(false))
        {
            await EndStreamAsync(context, board, line, attachment, stream).ConfigureAwait(false);
            return;
        }

        var pump = PumpAsync(stream, attachment.Reader, board.Options.HeartbeatInterval, context.RequestAborted);

        try
        {
            await board.RunLifecycleAsync(line, context, context.RequestServices, Lifecycle.Reconnected).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed welcome-back is not worth losing the line over.
            Logger(context).LogError(ex, "OnReconnectedAsync failed for line {LineId}", line.Id);
        }

        await pump.ConfigureAwait(false);
        await EndStreamAsync(context, board, line, attachment, stream).ConfigureAwait(false);
    }

    static async ValueTask<ServerSentEventStream?> StartStreamAsync(HttpContext context, BoardRuntime board, Line line)
    {
        try
        {
            return await ServerSentEventStream.StartAsync(context, cancellationToken: context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // Gone before the headers were out. A new line that never reached its client is not a
            // line anyone will come back for — unless it can be resumed, in which case the sweeper
            // decides like it does for any other drop.
            if (board.Options.ResumeWindow <= TimeSpan.Zero)
                board.Close(line, DisconnectReason.Dropped, null, allowReconnect: false);

            return null;
        }
    }

    /// <summary>
    /// Drains the line's queue onto the stream until the queue completes (the line closed, or a newer
    /// stream replaced this one) or the client goes away. Idle gaps get a heartbeat comment.
    /// </summary>
    static async Task PumpAsync(ServerSentEventStream stream, ChannelReader<Outbound> reader, TimeSpan heartbeat, CancellationToken aborted)
    {
        Task<bool>? waiting = null;

        try
        {
            while (true)
            {
                waiting ??= reader.WaitToReadAsync(aborted).AsTask();

                if (heartbeat > TimeSpan.Zero && !waiting.IsCompleted)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(aborted);
                    var delay = Task.Delay(heartbeat, idle.Token);

                    if (await Task.WhenAny(waiting, delay).ConfigureAwait(false) != waiting)
                    {
                        await stream.SendHeartbeatAsync(aborted).ConfigureAwait(false);
                        continue;
                    }

                    await idle.CancelAsync().ConfigureAwait(false);
                }

                if (!await waiting.ConfigureAwait(false))
                    return;

                waiting = null;

                while (reader.TryRead(out var item))
                {
                    await stream.SendAsync(
                        new ServerSentEvent
                        {
                            Id = item.Id.ToString(CultureInfo.InvariantCulture),
                            Event = item.Event,
                            Data = item.Data
                        },
                        aborted
                    ).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The client went away. That is how most streams end.
        }
    }

    /// <summary>
    /// After a stream's pump returns: sends the close event if the line was closed while this stream
    /// was current, otherwise marks the line detached so it can be resumed — or closes it, when there
    /// is no resume window.
    /// </summary>
    static async ValueTask EndStreamAsync(HttpContext context, BoardRuntime board, Line line, Attachment attachment, ServerSentEventStream stream)
    {
        if (line.CloseDataFor(attachment.Generation) is { } closeData)
        {
            await TrySendAsync(stream, SwitchboardProtocol.CloseEvent, closeData, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (line.IsClosed)
            return;

        line.Detach(attachment.Generation);

        if (board.Options.ResumeWindow <= TimeSpan.Zero && line.DetachedAt is not null)
            board.Close(line, DisconnectReason.Dropped, null, allowReconnect: false);
    }

    static async ValueTask<bool> TrySendAsync(ServerSentEventStream stream, string eventName, string data, CancellationToken cancellationToken)
    {
        try
        {
            await stream.SendAsync(eventName, data, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    static long ParseLastEventId(HttpContext context)
    {
        var raw = context.Request.Headers.GetFirst("Last-Event-ID")
            ?? context.Request.Query.GetFirst(SwitchboardProtocol.LastEventIdQuery);

        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
    }

    // ---- POST {base}/invoke ----

    public static async ValueTask InvokeAsync(HttpContext context, BoardRuntime board)
    {
        var line = await RequireLineAsync(context, board).ConfigureAwait(false);
        if (line is null)
            return;

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(context.Request.Body, default, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await SwitchboardResponses.ErrorAsync(context, StatusCodes.Status400BadRequest, "The invocation body is not valid JSON").ConfigureAwait(false);
            return;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("target", out var targetElement)
                || targetElement.ValueKind != JsonValueKind.String
                || targetElement.GetString() is not { Length: > 0 } target)
            {
                await SwitchboardResponses.ErrorAsync(context, StatusCodes.Status400BadRequest, "The invocation has no target").ConfigureAwait(false);
                return;
            }

            var args = root.TryGetProperty("args", out var argsElement) ? argsElement : default;
            if (args.ValueKind is not (JsonValueKind.Array or JsonValueKind.Undefined or JsonValueKind.Null))
            {
                await SwitchboardResponses.ErrorAsync(context, StatusCodes.Status400BadRequest, "The invocation's args must be an array").ConfigureAwait(false);
                return;
            }

            var method = board.Descriptor.FindMethod(target);
            if (method is null)
            {
                await SwitchboardResponses.ErrorAsync(context, StatusCodes.Status404NotFound, $"There is no method '{target}'").ConfigureAwait(false);
                return;
            }

            var count = args.ValueKind == JsonValueKind.Array ? args.GetArrayLength() : 0;
            if (count < method.RequiredArguments || count > method.TotalArguments)
            {
                var expected = method.RequiredArguments == method.TotalArguments
                    ? method.TotalArguments.ToString(CultureInfo.InvariantCulture)
                    : $"{method.RequiredArguments} to {method.TotalArguments}";

                await SwitchboardResponses.ErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    $"'{target}' takes {expected} argument(s), not {count}"
                ).ConfigureAwait(false);
                return;
            }

            if (!await SwitchboardAuthorization.AuthorizeAsync(context, RouteAuthorization(context, board), SwitchboardAuthorization.Fold(method.Metadata)).ConfigureAwait(false))
                return;

            long? sequence = root.TryGetProperty("seq", out var seqElement) && seqElement.TryGetInt64(out var seq) ? seq : null;

            await RunAsync(context, board, line, method, args, sequence).ConfigureAwait(false);
        }
    }

    static async ValueTask RunAsync(HttpContext context, BoardRuntime board, Line line, SwitchboardMethod method, JsonElement args, long? sequence)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, line.Closed);

        // With one call at a time, calls also run in the order the client numbered them: they are
        // separate requests, and on HTTP/2 especially nothing stops a later one overtaking.
        var ordered = sequence is not null && line.MaxParallelInvocations == 1;
        var holdsGate = false;
        var released = 0;

        void Release()
        {
            if (Interlocked.Exchange(ref released, 1) != 0)
                return;

            if (holdsGate)
                line.Gate.Release();

            if (ordered)
                line.EndTurn(sequence!.Value);
        }

        Switchboard? instance = null;
        var outcome = "ok";
        var started = Stopwatch.GetTimestamp();
        using var activity = SwitchboardMetrics.StartInvocation(board.Name, method.Name, line.Id);

        try
        {
            try
            {
                if (ordered)
                    await line.WaitTurnAsync(sequence!.Value, board.Options.InvocationOrderingTimeout, linked.Token).ConfigureAwait(false);

                await line.Gate.WaitAsync(linked.Token).ConfigureAwait(false);
                holdsGate = true;
            }
            catch (OperationCanceledException)
            {
                outcome = "cancelled";
                if (line.IsClosed)
                    await Gone(context).ConfigureAwait(false);

                return;
            }

            instance = board.Descriptor.Factory(context.RequestServices);

            var caller = new CallerContext(line, board, context, context.User);
            instance.Attach(caller, board);

            // A stream can run for as long as the client keeps reading. Once its headers are out it
            // no longer needs the line to itself, so it hands the gate and its turn back.
            var invocation = new SwitchboardInvocation(context, caller, args, linked.Token, board.Options)
            {
                StreamStarted = Release
            };

            Func<SwitchboardInvocationContext, ValueTask> next = c => method.Handler(c.Switchboard, invocation);
            var filters = board.Filters;
            for (var i = filters.Count - 1; i >= 0; i--)
            {
                var (filter, inner) = (filters[i], next);
                next = c => filter.InvokeMethodAsync(c, inner);
            }

            await next(new SwitchboardInvocationContext(instance, method.Name, args, context.RequestServices, linked.Token)).ConfigureAwait(false);

            // A filter that declined to call through and did not throw has nothing to say.
            if (!invocation.Responded && !context.Response.HasStarted)
                await invocation.CompleteAsync().ConfigureAwait(false);
        }
        catch (SwitchboardException ex)
        {
            outcome = "error";
            await SwitchboardResponses.ErrorAsync(context, StatusCodes.Status400BadRequest, ex.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            outcome = "cancelled";
            if (line.IsClosed)
                await Gone(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            outcome = "fault";
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            Logger(context).LogError(ex, "Switchboard method {Method} failed on line {LineId}", method.Name, line.Id);

            var message = board.Options.EnableDetailedErrors ? ex.Message : SwitchboardProtocol.GenericError;
            await SwitchboardResponses.ErrorAsync(context, StatusCodes.Status500InternalServerError, message).ConfigureAwait(false);
        }
        finally
        {
            Release();

            if (instance is not null)
                await BoardRuntime.DisposeBoardAsync(instance).ConfigureAwait(false);

            activity?.SetTag("switchboard.outcome", outcome);
            SwitchboardMetrics.Invocation(board.Name, method.Name, outcome, Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    // ---- POST {base}/completion ----

    /// <summary>The client's answer to a server → client call that is waiting on one.</summary>
    public static async ValueTask CompleteAsync(HttpContext context, BoardRuntime board)
    {
        var line = await RequireLineAsync(context, board).ConfigureAwait(false);
        if (line is null)
            return;

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(context.Request.Body, default, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await SwitchboardResponses.ErrorAsync(context, StatusCodes.Status400BadRequest, "The completion body is not valid JSON").ConfigureAwait(false);
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("invocationId", out var idElement)
                || idElement.GetString() is not { Length: > 0 } invocationId)
            {
                await SwitchboardResponses.ErrorAsync(context, StatusCodes.Status400BadRequest, "The completion has no invocationId").ConfigureAwait(false);
                return;
            }

            // Cloned: the document is disposed as soon as this returns, and the waiting call reads
            // the value after that.
            var found = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? line.FailResult(invocationId, error.GetString()!)
                : line.CompleteResult(invocationId, root.TryGetProperty("result", out var result) ? result.Clone() : default);

            // Late, duplicate or unknown answers are ignored — a 404, never a fault.
            if (!found)
            {
                await SwitchboardResponses.ErrorAsync(context, StatusCodes.Status404NotFound, "Nothing is waiting on that invocation").ConfigureAwait(false);
                return;
            }
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
        context.Response.ContentLength = 0;
        await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
    }

    // ---- DELETE {base}/connect ----

    public static async ValueTask HangUpAsync(HttpContext context, BoardRuntime board)
    {
        var token = context.Request.Headers.GetFirst(SwitchboardProtocol.LineHeader)
            ?? context.Request.Query.GetFirst(SwitchboardProtocol.TokenQuery);

        if (string.IsNullOrEmpty(token))
        {
            await SwitchboardResponses.ErrorAsync(context, StatusCodes.Status400BadRequest, $"The {SwitchboardProtocol.LineHeader} header is required").ConfigureAwait(false);
            return;
        }

        // Idempotent: hanging up a line that is already gone is the outcome the caller wanted.
        if (board.FindByToken(token) is { } line)
        {
            if (!await IsSameCallerAsync(context, line).ConfigureAwait(false))
                return;

            board.Close(line, DisconnectReason.ClientClosed, null, allowReconnect: false);
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
        context.Response.ContentLength = 0;
        await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
    }

    // ---- shared ----

    static async ValueTask<Line?> RequireLineAsync(HttpContext context, BoardRuntime board)
    {
        var token = context.Request.Headers.GetFirst(SwitchboardProtocol.LineHeader);
        if (string.IsNullOrEmpty(token))
        {
            await SwitchboardResponses.ErrorAsync(context, StatusCodes.Status400BadRequest, $"The {SwitchboardProtocol.LineHeader} header is required").ConfigureAwait(false);
            return null;
        }

        var line = board.FindByToken(token);
        if (line is null)
        {
            await Gone(context).ConfigureAwait(false);
            return null;
        }

        return await IsSameCallerAsync(context, line).ConfigureAwait(false) ? line : null;
    }

    /// <summary>
    /// The token proves "this line"; the auth pipeline proves "this user". A request carrying a line's
    /// token must also be the principal that opened the line, so a leaked token alone is not enough
    /// to speak as someone else.
    /// </summary>
    static async ValueTask<bool> IsSameCallerAsync(HttpContext context, Line line)
    {
        var name = context.User.Identity?.Name;
        if (string.Equals(name, line.PrincipalName, StringComparison.Ordinal))
            return true;

        await SwitchboardAuthorization.DenyAsync(context, "This line belongs to a different user").ConfigureAwait(false);
        return false;
    }

    static AuthorizationMetadata? RouteAuthorization(HttpContext context, BoardRuntime board)
        => context.Endpoint?.GetMetadata<AuthorizationMetadata>() ?? board.ClassAuthorization;

    static ValueTask Gone(HttpContext context)
        => SwitchboardResponses.ErrorAsync(context, 410, "The line is closed or unknown");

    static ILogger Logger(HttpContext context)
        => context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("Shiny.Net.HttpServer.Switchboard")
            ?? NullLogger.Instance;
}

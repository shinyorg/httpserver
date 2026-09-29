using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Shiny.Net.HttpServer.Sse;
using Shiny.Net.HttpServer.Switchboard.Internal;

namespace Shiny.Net.HttpServer.Switchboard;

// Everything in this file is what generated code calls. It is public because generated code lives in
// the app's assembly and has to reach it — none of it is an API worth calling by hand.

/// <summary>Runs one switchboard method against an already-created instance. Generated.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public delegate ValueTask SwitchboardMethodHandler(Switchboard board, SwitchboardInvocation invocation);

/// <summary>One callable method on a switchboard. Generated.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class SwitchboardMethod(
    string name,
    int requiredArguments,
    int totalArguments,
    IReadOnlyList<object> metadata,
    SwitchboardMethodHandler handler
)
{
    /// <summary>The name clients call it by.</summary>
    public string Name { get; } = name;

    /// <summary>JSON arguments without a default value.</summary>
    public int RequiredArguments { get; } = requiredArguments;

    /// <summary>All JSON arguments. Services, the cancellation token and the caller context are not counted.</summary>
    public int TotalArguments { get; } = totalArguments;

    /// <summary><c>[Authorize]</c> / <c>[AllowAnonymous]</c> on the method.</summary>
    public IReadOnlyList<object> Metadata { get; } = metadata;

    public SwitchboardMethodHandler Handler { get; } = handler;
}

/// <summary>Everything the runtime needs to know about one switchboard type. Generated.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class SwitchboardDescriptor
{
    readonly Dictionary<string, SwitchboardMethod> methods;

    public SwitchboardDescriptor(
        Type boardType,
        Func<IServiceProvider, Switchboard> factory,
        IReadOnlyList<object> metadata,
        IReadOnlyList<SwitchboardMethod> methods
    )
    {
        ArgumentNullException.ThrowIfNull(boardType);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(methods);

        this.BoardType = boardType;
        this.Factory = factory;
        this.Metadata = metadata;
        this.methods = new Dictionary<string, SwitchboardMethod>(methods.Count, StringComparer.Ordinal);

        foreach (var method in methods)
            this.methods[method.Name] = method;
    }

    public Type BoardType { get; }

    /// <summary>Builds an instance from a service scope — constructor injection, written out at compile time.</summary>
    public Func<IServiceProvider, Switchboard> Factory { get; }

    /// <summary><c>[Authorize]</c> / <c>[AllowAnonymous]</c> on the class. Applied to every route and every call.</summary>
    public IReadOnlyList<object> Metadata { get; }

    public IReadOnlyCollection<SwitchboardMethod> Methods => this.methods.Values;

    public SwitchboardMethod? FindMethod(string name) => this.methods.GetValueOrDefault(name);
}

/// <summary>
/// Where generated dispatchers put themselves, from a module initializer. Keyed by switchboard type,
/// which is what <c>MapSwitchboard&lt;TBoard&gt;</c> and <c>IOperator&lt;TBoard&gt;</c> look up.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class SwitchboardRegistry
{
    static readonly ConcurrentDictionary<Type, SwitchboardDescriptor> Descriptors = new();

    public static void Register(SwitchboardDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Descriptors[descriptor.BoardType] = descriptor;
    }

    public static bool TryGet(Type boardType, out SwitchboardDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(boardType);

        if (Descriptors.TryGetValue(boardType, out descriptor!))
            return true;

        // A module initializer runs when something in its module is first *executed*, and naming a
        // type with typeof() is not that. A switchboard declared in a class library could therefore
        // be looked up before its registration has run; running the module constructor here closes
        // that gap. It is a no-op when the initializer has already run.
        RuntimeHelpers.RunModuleConstructor(boardType.Module.ModuleHandle);
        return Descriptors.TryGetValue(boardType, out descriptor!);
    }

    public static SwitchboardDescriptor GetRequired(Type boardType)
        => TryGet(boardType, out var descriptor)
            ? descriptor
            : throw new InvalidOperationException(
                $"'{boardType.FullName}' has no generated switchboard dispatcher. The Switchboard source " +
                "generator writes one for every non-abstract, non-generic class deriving from Switchboard " +
                "in a project that references Shiny.Net.HttpServer.Switchboard — check the build for " +
                "SWB diagnostics against it."
            );
}

/// <summary>One call in progress. What a generated method handler reads its arguments from and writes its result to.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class SwitchboardInvocation
{
    readonly HttpContext httpContext;
    readonly JsonElement arguments;
    readonly SwitchboardOptions options;

    internal SwitchboardInvocation(
        HttpContext httpContext,
        CallerContext caller,
        JsonElement arguments,
        CancellationToken aborted,
        SwitchboardOptions options
    )
    {
        this.httpContext = httpContext;
        this.Caller = caller;
        this.arguments = arguments;
        this.Aborted = aborted;
        this.options = options;
        this.ArgumentCount = arguments.ValueKind == JsonValueKind.Array ? arguments.GetArrayLength() : 0;
    }

    /// <summary>The request's service scope.</summary>
    public IServiceProvider Services => this.httpContext.RequestServices;

    public CallerContext Caller { get; }

    /// <summary>Cancelled when the client abandons the call or the line closes for good.</summary>
    public CancellationToken Aborted { get; }

    public int ArgumentCount { get; }

    internal bool Responded { get; private set; }

    /// <summary>Called once a streamed response has started, so the call can stop holding the line.</summary>
    internal Action? StreamStarted { get; init; }

    /// <summary>Reads a positional argument with the JSON metadata registered for <typeparamref name="T"/>.</summary>
    public T GetArgument<T>(int index, string name)
    {
        if (index >= this.ArgumentCount)
            throw new SwitchboardArgumentException($"Argument '{name}' is missing.");

        try
        {
            return JsonSerializer.Deserialize(this.arguments[index], SwitchboardJson.Get<T>())!;
        }
        catch (JsonException ex)
        {
            throw new SwitchboardArgumentException($"Argument '{name}' is not a valid {typeof(T).Name}.", ex);
        }
    }

    /// <summary>Reads an optional positional argument, falling back to its declared default.</summary>
    public T GetArgument<T>(int index, string name, T defaultValue)
        => index < this.ArgumentCount ? this.GetArgument<T>(index, name) : defaultValue;

    /// <summary>The method returned nothing: answers 204.</summary>
    public ValueTask CompleteAsync()
    {
        this.Responded = true;

        var response = this.httpContext.Response;
        response.StatusCode = StatusCodes.Status204NoContent;
        response.ContentLength = 0;

        return response.StartAsync(this.Aborted);
    }

    /// <summary>The method returned a value: answers 200 with <c>{"result": …}</c>.</summary>
    public async ValueTask ReturnAsync<T>(T value)
    {
        this.Responded = true;

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("result");

            if (value is null)
                writer.WriteNullValue();
            else
                JsonSerializer.Serialize(writer, value, SwitchboardJson.Get<T>());

            writer.WriteEndObject();
        }

        var response = this.httpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;

        await response.WriteBytesAsync(buffer.WrittenMemory, "application/json", this.Aborted).ConfigureAwait(false);
    }

    /// <summary>
    /// The method returned a stream: the response becomes an event stream with one <c>item</c> per
    /// element, then <c>complete</c> — or <c>error</c>, since a status code can no longer change once
    /// the first item is out.
    /// </summary>
    public async ValueTask StreamAsync<T>(IAsyncEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        this.Responded = true;

        // Resolved before the headers go out, so a type with no metadata is still a clean error
        // response rather than a stream that dies on its first item.
        var typeInfo = SwitchboardJson.Get<T>();
        var stream = await ServerSentEventStream.StartAsync(this.httpContext, cancellationToken: this.Aborted).ConfigureAwait(false);
        this.StreamStarted?.Invoke();

        try
        {
            await foreach (var item in items.WithCancellation(this.Aborted).ConfigureAwait(false))
            {
                var json = item is null ? "null" : JsonSerializer.Serialize(item, typeInfo);
                await stream.SendAsync(SwitchboardProtocol.StreamItemEvent, json, this.Aborted).ConfigureAwait(false);
            }

            await stream.SendAsync(SwitchboardProtocol.StreamCompleteEvent, string.Empty, this.Aborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (this.Aborted.IsCancellationRequested)
        {
            // The client stopped reading or the line closed. Nobody is left to tell.
        }
        catch (Exception ex)
        {
            var message = ex is SwitchboardException || this.options.EnableDetailedErrors
                ? ex.Message
                : SwitchboardProtocol.GenericError;

            var json = JsonSerializer.Serialize(new ErrorPayload(message), SwitchboardProtocolJson.Default.ErrorPayload);
            await stream.SendAsync(SwitchboardProtocol.StreamErrorEvent, json, this.Aborted).ConfigureAwait(false);
        }
    }
}

/// <summary>An argument that could not be bound. Answered with a 400.</summary>
sealed class SwitchboardArgumentException : SwitchboardException
{
    public SwitchboardArgumentException(string message) : base(message)
    {
    }

    public SwitchboardArgumentException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}

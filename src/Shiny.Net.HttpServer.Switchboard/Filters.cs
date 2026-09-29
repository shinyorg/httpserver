using System.Text.Json;

namespace Shiny.Net.HttpServer.Switchboard;

/// <summary>
/// Wraps switchboard calls and lifecycle callbacks — the switchboard counterpart of middleware, for
/// logging, validation, auditing, or refusing a call. Implement only the members you need; the rest
/// pass straight through.
/// <code>
/// public sealed class AuditFilter(ILogger&lt;AuditFilter&gt; logger) : ISwitchboardFilter
/// {
///     public async ValueTask InvokeMethodAsync(SwitchboardInvocationContext context, Func&lt;SwitchboardInvocationContext, ValueTask&gt; next)
///     {
///         logger.LogInformation("{User} called {Method}", context.Caller.UserIdentifier, context.MethodName);
///         await next(context);
///     }
/// }
///
/// builder.AddSwitchboardFilter&lt;AuditFilter&gt;();
/// </code>
/// To refuse a call, throw <see cref="SwitchboardException"/> instead of calling <c>next</c>: the
/// client gets its message.
/// </summary>
public interface ISwitchboardFilter
{
    ValueTask InvokeMethodAsync(SwitchboardInvocationContext context, Func<SwitchboardInvocationContext, ValueTask> next)
        => next(context);

    Task OnConnectedAsync(SwitchboardLifecycleContext context, Func<SwitchboardLifecycleContext, Task> next)
        => next(context);

    Task OnReconnectedAsync(SwitchboardLifecycleContext context, Func<SwitchboardLifecycleContext, Task> next)
        => next(context);

    Task OnDisconnectedAsync(
        SwitchboardLifecycleContext context,
        DisconnectContext disconnect,
        Func<SwitchboardLifecycleContext, DisconnectContext, Task> next
    ) => next(context, disconnect);
}

/// <summary>One call passing through the filters.</summary>
public sealed class SwitchboardInvocationContext
{
    internal SwitchboardInvocationContext(Switchboard board, string methodName, JsonElement arguments, IServiceProvider services, CancellationToken aborted)
    {
        this.Switchboard = board;
        this.MethodName = methodName;
        this.Arguments = arguments;
        this.Services = services;
        this.Aborted = aborted;
    }

    /// <summary>The instance the call will run on.</summary>
    public Switchboard Switchboard { get; }

    public CallerContext Caller => this.Switchboard.Context;

    /// <summary>The name the client called, after any <c>[LineMethod]</c> rename.</summary>
    public string MethodName { get; }

    /// <summary>The raw JSON arguments, as the client sent them. Undefined when it sent none.</summary>
    public JsonElement Arguments { get; }

    /// <summary>The request's service scope.</summary>
    public IServiceProvider Services { get; }

    public CancellationToken Aborted { get; }
}

/// <summary>One lifecycle callback passing through the filters.</summary>
public sealed class SwitchboardLifecycleContext
{
    internal SwitchboardLifecycleContext(Switchboard board, IServiceProvider services)
    {
        this.Switchboard = board;
        this.Services = services;
    }

    public Switchboard Switchboard { get; }

    public CallerContext Caller => this.Switchboard.Context;

    public IServiceProvider Services { get; }
}

/// <summary>
/// Publishes a switchboard's contract — the interface clients call it through, the <c>TClient</c>
/// interface it calls them through, the data types both mention, and a JSON context for them — as
/// one C# file, for clients that cannot reference a shared contracts assembly.
/// <code>
/// [ExportContract(Namespace = "Chat.Contracts")]
/// public class ChatBoard : Switchboard&lt;IChatClient&gt; { … }
/// </code>
/// Set <c>&lt;SwitchboardContractsOutputPath&gt;</c> in the project file to where the file should be
/// copied after each build; it is rewritten only when its content changes.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ExportContractAttribute : Attribute
{
    /// <summary>The namespace of the exported types. Defaults to the switchboard's own namespace plus <c>.Contracts</c>.</summary>
    public string? Namespace { get; set; }

    /// <summary>The name of the client → server interface. Defaults to <c>I</c> plus the switchboard's name.</summary>
    public string? BoardInterface { get; set; }
}

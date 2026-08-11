namespace Shiny.Net.HttpServer;

/// <summary>
/// The current request's context, for services that are resolved from the container rather than
/// handed a context.
/// <para>
/// Use it sparingly. A service that reaches for the ambient request is harder to test and harder to
/// reason about than one that is given what it needs — this exists so that <c>ISession</c> and
/// friends can be injected as scoped services, not as a general escape hatch.
/// </para>
/// </summary>
public interface IHttpContextAccessor
{
    HttpContext? HttpContext { get; }
}

/// <summary>
/// Publishes the current context through an <see cref="AsyncLocal{T}"/>.
/// <para>
/// The indirection through a holder is deliberate: an <see cref="AsyncLocal{T}"/> assigned directly
/// would leak into every async continuation started by the request and keep the context alive after
/// it finished. Clearing the holder's field severs that for every copy at once — which matters here
/// because contexts are pooled and reused by the next request on the connection.
/// </para>
/// </summary>
public sealed class HttpContextAccessor : IHttpContextAccessor
{
    static readonly AsyncLocal<ContextHolder> Current = new();

    public HttpContext? HttpContext
    {
        get => Current.Value?.Context;

        internal set
        {
            if (Current.Value is { } existing)
                existing.Context = null;

            if (value is not null)
                Current.Value = new ContextHolder { Context = value };
        }
    }

    /// <summary>Sets the ambient context for the current async flow.</summary>
    internal static void Set(HttpContext? context)
    {
        if (Current.Value is { } existing)
            existing.Context = null;

        if (context is not null)
            Current.Value = new ContextHolder { Context = context };
    }

    sealed class ContextHolder
    {
        public HttpContext? Context;
    }
}

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.Net.HttpServer;

/// <summary>
/// Registers an options type so that every call contributes to it. This is the idiom for the <c>Add…</c>
/// methods of this library, and of any package that builds on <see cref="ShinyHttpServerBuilder"/>.
/// <code>
/// public static ShinyHttpServerBuilder AddWidgets(this ShinyHttpServerBuilder builder, Action&lt;WidgetOptions&gt;? configure = null)
/// {
///     OptionsRegistration.Configure(builder.Services, configure);
///     return builder;
/// }
/// </code>
/// <para>
/// It replaces <c>TryAddSingleton</c> around a factory that runs the one <c>configure</c> it captured. That
/// keeps the first call and drops every later one without a word — a policy added by a second
/// <c>AddAuthorization</c> simply does not exist, and the first request that names it is a 500. The builder
/// already promises that a second registration adopts what the first put there rather than configuring
/// something nothing reads; this is that promise for options.
/// </para>
/// </summary>
public static class OptionsRegistration
{
    /// <summary>
    /// Registers <typeparamref name="TOptions"/> as a singleton the first time, and adds
    /// <paramref name="configure"/> to what is applied when it is resolved — every call's, in call order.
    /// </summary>
    /// <param name="services">The collection the options live in.</param>
    /// <param name="configure">This call's contribution. Null registers the options and adds nothing.</param>
    /// <param name="complete">
    /// Runs once, after every <paramref name="configure"/>, for work that needs the finished options —
    /// building validation from them, say. The first one given is used.
    /// </param>
    /// <remarks>
    /// An app that registered its own <typeparamref name="TOptions"/> instance before calling this keeps
    /// it, and the configure actions are not applied to it.
    /// </remarks>
    public static void Configure<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(
        IServiceCollection services,
        Action<TOptions>? configure,
        Action<TOptions>? complete = null
    ) where TOptions : class, new()
    {
        ArgumentNullException.ThrowIfNull(services);

        var registration = Existing<TOptions>(services);
        if (registration is null)
        {
            registration = new OptionsConfiguration<TOptions>();
            services.AddSingleton(registration);
            services.TryAddSingleton(_ => registration.Create());
        }

        if (configure is not null)
            registration.Actions.Add(configure);

        registration.Complete ??= complete;
    }

    static OptionsConfiguration<TOptions>? Existing<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(
        IServiceCollection services
    ) where TOptions : class, new()
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(OptionsConfiguration<TOptions>) && descriptor.ImplementationInstance is OptionsConfiguration<TOptions> existing)
                return existing;
        }

        return null;
    }
}

/// <summary>
/// Every configure action registered for one options type, held in the container so that each call — from any
/// builder over the same collection — adds to the same list.
/// </summary>
sealed class OptionsConfiguration<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>
    where TOptions : class, new()
{
    public List<Action<TOptions>> Actions { get; } = [];

    public Action<TOptions>? Complete { get; set; }

    public TOptions Create()
    {
        var options = new TOptions();

        foreach (var configure in this.Actions)
            configure(options);

        this.Complete?.Invoke(options);
        return options;
    }
}

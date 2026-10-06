using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.Net.Http;

namespace Shiny.Net.HttpServer.FileSync.Client;

public static class FileSyncServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="FileSyncClient"/> as a singleton.
    /// <code>
    /// builder.Services.AddFileSyncClient(o =>
    /// {
    ///     o.ServerUri = new("https://files.example.com/sync");
    ///     o.LocalPath = Path.Combine(FileSystem.AppDataDirectory, "Documents");
    ///     o.Headers["Authorization"] = "Bearer " + token;
    /// });
    /// </code>
    /// <para>
    /// Background transfers come from Shiny.Net.Http. If the app has not registered them, this does
    /// (<c>AddHttpTransfers</c>, with a delegate that retries failed transfers) - on iOS, Android and
    /// Windows that is the platform's background transfer service. An app that uses Shiny.Net.Http
    /// for its own transfers registers them with its own delegate <em>before</em> this call, and the
    /// sync client shares them. Where no transfer manager exists - plain .NET on macOS or Linux
    /// without <c>AddHttpClientTransfers</c> - chunks move through <see cref="HttpClient"/> instead.
    /// </para>
    /// </summary>
    public static IServiceCollection AddFileSyncClient(this IServiceCollection services, Action<FileSyncClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new FileSyncClientOptions();
        configure(options);
        options.Validate();

        if (options.UseBackgroundTransfers
            && !services.Any(d => d.ServiceType == typeof(IHttpTransferManager) || d.ServiceType == typeof(IHttpTransferDelegate)))
        {
            Shiny.HttpTransferServiceCollectionExtensions.AddHttpTransfers<FileSyncTransferDelegate>(services);
        }

        services.AddSingleton(options);
        services.AddSingleton(sp => new FileSyncClient(
            options,
            sp.GetService<IHttpTransferManager>(),
            null,
            sp.GetService<ILogger<FileSyncClient>>()
        ));

        return services;
    }
}

/// <summary>
/// The transfer delegate registered when the app has none: Shiny's own retry logic, and nothing
/// else - the sync client watches its transfers itself.
/// </summary>
sealed class FileSyncTransferDelegate(ILogger<FileSyncTransferDelegate> logger, IHttpTransferManager manager)
    : HttpTransferDelegate(logger, manager, 3)
{
    public override Task OnCompleted(HttpTransferRequest request) => Task.CompletedTask;
}

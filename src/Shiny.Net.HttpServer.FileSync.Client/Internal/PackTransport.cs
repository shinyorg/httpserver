using Shiny.Net.Http;

namespace Shiny.Net.HttpServer.FileSync.Client.Internal;

/// <summary>How packs move: through Shiny.Net.Http's background transfers, or straight through HttpClient.</summary>
interface IPackTransport
{
    /// <summary>Uploads a pack file. Returns once the server has stored every chunk in it.</summary>
    Task UploadAsync(string identifier, string packFile, IDictionary<string, string> headers, CancellationToken cancellationToken);

    /// <summary>Downloads a pack to <paramref name="destination"/>.</summary>
    Task DownloadAsync(string identifier, Uri uri, string destination, IDictionary<string, string> headers, CancellationToken cancellationToken);
}

/// <summary>
/// Through <see cref="IHttpTransferManager"/>: on iOS an <c>NSURLSession</c> background session, on
/// Android a foreground service, elsewhere a managed loop - and in every case the transfer outlives
/// the pass that started it. Identifiers are derived from the pack's content, so a pass that runs
/// after the app was killed mid-transfer finds the transfer still going (or done) and waits on it
/// instead of starting another.
/// </summary>
sealed class ShinyPackTransport(IHttpTransferManager manager, FileSyncClientOptions options) : IPackTransport
{
    public Task UploadAsync(string identifier, string packFile, IDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        HttpTransferRequest request;

        if (options.UseTusUploads)
        {
            var tus = new TusUploadRequest(packFile) { Identifier = identifier }
                .WithEndpoint(Endpoints.Combine(options.ServerUri, "uploads").ToString())
                .WithoutFileName();

            if (options.UseMeteredConnection)
                tus = tus.WithMeteredConnection();

            foreach (var (key, value) in headers)
                tus = tus.WithHeader(key, value);

            request = tus.Build();
        }
        else
        {
            request = new HttpTransferRequest(
                identifier,
                Endpoints.Combine(options.ServerUri, "chunks/pack").ToString(),
                TransferType.UploadRaw,
                packFile,
                options.UseMeteredConnection,
                null,
                new Dictionary<string, string>(headers)
            )
            {
                HttpMethod = "PUT"
            };
        }

        return this.RunAsync(request, cancellationToken);
    }

    public Task DownloadAsync(string identifier, Uri uri, string destination, IDictionary<string, string> headers, CancellationToken cancellationToken)
        => this.RunAsync(new HttpTransferRequest(
            identifier,
            uri.ToString(),
            TransferType.Download,
            destination,
            options.UseMeteredConnection,
            null,
            new Dictionary<string, string>(headers)
        ), cancellationToken);

    async Task RunAsync(HttpTransferRequest request, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<HttpTransferResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnUpdate(object? sender, HttpTransferResult result)
        {
            if (result.Request.Identifier == request.Identifier && result.Status is HttpTransferState.Completed or HttpTransferState.Error or HttpTransferState.Canceled)
                done.TrySetResult(result);
        }

        // Subscribed before anything is queued, so a transfer that finishes at once is still seen.
        manager.UpdateReceived += OnUpdate;

        try
        {
            var existing = (await manager.GetTransfers().ConfigureAwait(false)).FirstOrDefault(t => t.Identifier == request.Identifier);

            if (existing is null)
            {
                await manager.Queue(request).ConfigureAwait(false);
            }
            else if (existing.Status is HttpTransferState.Paused)
            {
                // Paused by someone, left in the queue: this pass needs it, so carry on with it.
                await manager.Resume(request.Identifier).ConfigureAwait(false);
            }

            using (cancellationToken.Register(() => done.TrySetCanceled(cancellationToken)))
            {
                var result = await done.Task.ConfigureAwait(false);

                if (result.Status != HttpTransferState.Completed)
                    throw new HttpRequestException($"Transfer {request.Identifier} ended {result.Status}.", result.Exception);
            }
        }
        finally
        {
            manager.UpdateReceived -= OnUpdate;
        }
    }
}

/// <summary>Straight through <see cref="HttpClient"/>: while the app runs, without the background machinery.</summary>
sealed class DirectPackTransport(HttpClient http, FileSyncClientOptions options) : IPackTransport
{
    public async Task UploadAsync(string identifier, string packFile, IDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(packFile);
        using var request = new HttpRequestMessage(HttpMethod.Put, Endpoints.Combine(options.ServerUri, "chunks/pack"))
        {
            Content = new StreamContent(file)
        };

        request.Content.Headers.ContentType = new(PackFormat.ContentType);

        foreach (var (key, value) in headers)
            request.Headers.TryAddWithoutValidation(key, value);

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task DownloadAsync(string identifier, Uri uri, string destination, IDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        foreach (var (key, value) in headers)
            request.Headers.TryAddWithoutValidation(key, value);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
    }
}

static class Endpoints
{
    /// <summary>The sync endpoint's URL plus a relative route, whatever the configured URL's trailing slash.</summary>
    public static Uri Combine(Uri server, string relative)
        => new(server.ToString().TrimEnd('/') + "/" + relative);
}

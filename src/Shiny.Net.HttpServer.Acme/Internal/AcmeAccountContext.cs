using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer.Acme.Internal;

/// <summary>
/// What several certificates issued by one server share: the ACME account, the store it lives in, the
/// HTTP client that talks to the CA, and the challenge responder the server answers from.
/// <para>
/// A lone <see cref="AcmeCertificateManager"/> owns one of these. An <see cref="AcmeCertificateRegistry"/>
/// owns one and hands it to every entry's manager, so a hundred certificates are still one account
/// (one registration, one rate-limit bucket for accounts), one connection pool to the CA, and one
/// HTTP-01 middleware that answers for all of them.
/// </para>
/// </summary>
sealed class AcmeAccountContext : IDisposable
{
    readonly SemaphoreSlim gate = new(1, 1);
    AcmeClient? client;

    public AcmeAccountContext(
        string directoryUrl,
        string? email,
        bool acceptTermsOfService,
        AcmeExternalAccountBinding? externalAccountBinding,
        IAcmeCertificateStore store,
        HttpMessageHandler? backchannelHandler
    )
    {
        this.DirectoryUrl = directoryUrl;
        this.Email = email;
        this.AcceptTermsOfService = acceptTermsOfService;
        this.ExternalAccountBinding = externalAccountBinding;
        this.Store = store;

        this.Http = backchannelHandler is { } handler
            ? new HttpClient(handler, disposeHandler: false)
            : new HttpClient();

        // RFC 8555 §6.1: clients must send a User-Agent. CAs use it to find the software behind a
        // misbehaving account.
        var version = typeof(AcmeCertificateManager).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        this.Http.DefaultRequestHeaders.UserAgent.ParseAdd($"Shiny.Net.HttpServer.Acme/{version}");
        this.Http.Timeout = TimeSpan.FromSeconds(60);
    }

    public static AcmeAccountContext From(AcmeOptions options) => new(
        options.DirectoryUrl,
        options.Email,
        options.AcceptTermsOfService,
        options.ExternalAccountBinding,
        options.Store ?? new DirectoryAcmeCertificateStore(options.ResolveStorePath()),
        options.BackchannelHttpHandler
    );

    public string DirectoryUrl { get; }
    public string? Email { get; }
    public bool AcceptTermsOfService { get; }
    public AcmeExternalAccountBinding? ExternalAccountBinding { get; }
    public IAcmeCertificateStore Store { get; }
    public HttpClient Http { get; }
    public AcmeChallengeResponder Challenges { get; } = new();

    /// <summary>
    /// The registered client, registering on first use. Serialized, so two certificates ordered at the
    /// same moment on a fresh store make one account rather than two racing to save theirs.
    /// </summary>
    public async Task<AcmeClient> GetClientAsync(ILogger logger, CancellationToken cancellationToken)
    {
        if (this.client is { } existing)
            return existing;

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.client is { } raced)
                return raced;

            var stored = await this.Store.LoadAccountAsync(this.DirectoryUrl, cancellationToken).ConfigureAwait(false);

            AcmeAccountKey key;
            if (stored is not null)
                key = AcmeAccountKey.Import(stored.PrivateKey);
            else
            {
                // Saved before registering: a crash between the CA creating the account and us recording it
                // would otherwise leave an account nobody holds the key to.
                key = AcmeAccountKey.Create();
                await this.SaveAccountAsync(key, null, cancellationToken).ConfigureAwait(false);
            }

            var client = new AcmeClient(this.Http, this.DirectoryUrl, key, stored?.Location, logger);
            await client.LoadDirectoryAsync(cancellationToken).ConfigureAwait(false);

            var (url, created) = await client
                .EnsureAccountAsync(this.Email, this.AcceptTermsOfService, this.ExternalAccountBinding, cancellationToken)
                .ConfigureAwait(false);

            if (url != stored?.Location)
            {
                await this.SaveAccountAsync(key, url, cancellationToken).ConfigureAwait(false);
                logger.LogInformation(
                    created ? "Registered ACME account {Account} with {Directory}{Terms}" : "Using existing ACME account {Account} with {Directory}{Terms}",
                    url,
                    this.DirectoryUrl,
                    client.Directory.TermsOfService is { } tos ? $"; terms of service accepted: {tos}" : ""
                );
            }

            this.client = client;
            return client;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>
    /// Forgets an account the CA no longer knows, keeping its key, so the next <see cref="GetClientAsync"/>
    /// registers it again. Only the client that failed is dropped: another certificate may already have
    /// re-registered and replaced it.
    /// </summary>
    public async Task ResetAsync(AcmeClient stale, CancellationToken cancellationToken)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(this.client, stale))
                return;

            this.client = null;
            await this.SaveAccountAsync(stale.Key, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    Task SaveAccountAsync(AcmeAccountKey key, string? location, CancellationToken cancellationToken)
        => this.Store.SaveAccountAsync(this.DirectoryUrl, new AcmeAccountData(key.ExportPkcs8(), location), cancellationToken);

    public void Dispose()
    {
        this.Http.Dispose();
        this.client?.Key.Dispose();
        this.gate.Dispose();
    }
}

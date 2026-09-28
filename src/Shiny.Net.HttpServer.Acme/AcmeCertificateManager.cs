using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Net.HttpServer.Acme.Internal;

namespace Shiny.Net.HttpServer.Acme;

/// <summary>
/// Owns the ACME certificate: loads it from the store, obtains it when there is none, renews it before
/// it expires, and swaps each new one into the running server.
/// <para>
/// The swap is the part that matters on a server that is never restarted. Every HTTPS endpoint wired to
/// this manager asks <see cref="SelectCertificate"/> on every handshake, so installing a certificate is a
/// single reference assignment: the next connection gets the new one, connections already open keep the
/// one they negotiated, and nothing is dropped. The HTTP/3 listener asks the same way.
/// </para>
/// <para>
/// Usually created by <c>builder.AddAcme(...)</c>, which also wires it to the server. Without a container:
/// <code>
/// var acme = new AcmeCertificateManager(new AcmeOptions { Domains = ["example.com"], AcceptTermsOfService = true });
/// server.UseAcme(acme);
/// </code>
/// </para>
/// </summary>
public sealed class AcmeCertificateManager : IAsyncDisposable
{
    readonly ILogger logger;
    readonly IAcmeCertificateStore store;
    readonly HttpClient http;
    readonly SemaphoreSlim issuing = new(1, 1);
    readonly object loopLock = new();

    volatile AcmeIssuedCertificate? current;
    AcmeClient? client;
    IReadOnlyList<AcmeIdentifier>? identifiers;
    CancellationTokenSource? loopStopping;
    Task? loop;
    (DateTimeOffset Start, DateTimeOffset End, DateTimeOffset Chosen)? renewalWindow;
    DateTimeOffset lastIssued = DateTimeOffset.MinValue;
    int disposed;

    public AcmeCertificateManager(AcmeOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        this.Options = options;
        this.logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<AcmeCertificateManager>();
        this.store = options.Store ?? new DirectoryAcmeCertificateStore(options.ResolveStorePath());

        this.http = options.BackchannelHttpHandler is { } handler
            ? new HttpClient(handler, disposeHandler: false)
            : new HttpClient();

        // RFC 8555 §6.1: clients must send a User-Agent. CAs use it to find the software behind a
        // misbehaving account.
        var version = typeof(AcmeCertificateManager).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        this.http.DefaultRequestHeaders.UserAgent.ParseAdd($"Shiny.Net.HttpServer.Acme/{version}");
        this.http.Timeout = TimeSpan.FromSeconds(60);
    }

    /// <summary>The options this manager was created with.</summary>
    public AcmeOptions Options { get; }

    /// <summary>The certificate being served, or null until one has been loaded or issued.</summary>
    public X509Certificate2? Certificate => this.current?.Certificate;

    /// <summary>The certificate being served, with its chain, as the TLS layer uses it.</summary>
    public SslStreamCertificateContext? CertificateContext => this.current?.Context;

    /// <summary>When the background loop next intends to renew, once it has looked.</summary>
    public DateTimeOffset? NextRenewal { get; private set; }

    /// <summary>
    /// Raised after a new certificate is installed — first load, first issuance, and every renewal. The
    /// server is already serving it when this fires. Handlers run on the renewal loop; keep them short.
    /// </summary>
    public event EventHandler<X509Certificate2>? CertificateChanged;

    /// <summary>
    /// The selector every wired endpoint calls per handshake: the current certificate context, or null
    /// before there is one. SNI is not consulted — this manager holds one certificate covering every
    /// configured name, and a client asking for some other name is better told so by the certificate than
    /// by a failed handshake.
    /// </summary>
    public SslStreamCertificateContext? SelectCertificate(string? serverName) => this.current?.Context;

    internal AcmeChallengeResponder Challenges { get; } = new();

    internal IAcmeCertificateStore Store => this.store;

    IReadOnlyList<AcmeIdentifier> Identifiers => this.identifiers ??= AcmeIdentifier.Parse(this.Options.Domains);

    string StorageName => AcmeCertificates.StorageName(this.Identifiers);

    /// <summary>
    /// Loads the stored certificate, if there is one that covers the configured names, and installs it.
    /// Returns null when there is nothing usable stored. Does not contact the CA.
    /// </summary>
    public async Task<X509Certificate2?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await this.LoadStoredAsync(cancellationToken).ConfigureAwait(false);
        return loaded?.Certificate;
    }

    /// <summary>
    /// Makes sure there is a certificate that is not due for renewal: loads the stored one, and orders a
    /// new one if it is missing or due. What the background loop does on each pass, for a caller that wants
    /// to await it — at startup, say, before announcing the server as ready.
    /// </summary>
    public async Task<X509Certificate2> EnsureCertificateAsync(CancellationToken cancellationToken = default)
    {
        var certificate = this.current ?? await this.LoadStoredAsync(cancellationToken).ConfigureAwait(false);

        if (certificate is not null)
        {
            var due = await this.ComputeRenewalAsync(certificate, cancellationToken).ConfigureAwait(false);
            if (due > DateTimeOffset.UtcNow)
                return certificate.Certificate;
        }

        return (await this.IssueAsync(cancellationToken).ConfigureAwait(false)).Certificate;
    }

    /// <summary>
    /// Orders a new certificate now, whatever the current one's age, and installs it. For a key
    /// compromise, a changed name list, or a CA's revocation notice.
    /// </summary>
    public async Task<X509Certificate2> RenewAsync(CancellationToken cancellationToken = default)
        => (await this.IssueAsync(cancellationToken).ConfigureAwait(false)).Certificate;

    /// <summary>
    /// Starts the background loop that obtains and renews the certificate. Idempotent. <c>UseAcme</c> calls
    /// this when the server reaches Running — HTTP-01 needs the server listening before the CA can
    /// validate anything.
    /// </summary>
    public void StartRenewal()
    {
        ObjectDisposedException.ThrowIf(this.disposed != 0, this);

        lock (this.loopLock)
        {
            if (this.loop is { IsCompleted: false })
                return;

            this.loopStopping = new CancellationTokenSource();
            var token = this.loopStopping.Token;
            this.loop = Task.Run(() => this.RunAsync(token), CancellationToken.None);
        }
    }

    /// <summary>Stops the background loop, waiting for an issuance in flight to notice. Idempotent.</summary>
    public async Task StopRenewalAsync()
    {
        Task? running;
        CancellationTokenSource? stopping;

        lock (this.loopLock)
        {
            running = this.loop;
            stopping = this.loopStopping;
            this.loop = null;
            this.loopStopping = null;
        }

        if (stopping is null)
            return;

        await stopping.CancelAsync().ConfigureAwait(false);

        if (running is not null)
        {
            try
            {
                await running.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        stopping.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        await this.StopRenewalAsync().ConfigureAwait(false);
        this.http.Dispose();
        this.client?.Key.Dispose();
        this.issuing.Dispose();
    }

    // ---- the loop ----

    async Task RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan wait;

            try
            {
                var certificate = this.current ?? await this.LoadStoredAsync(cancellationToken).ConfigureAwait(false);

                if (certificate is null)
                {
                    await this.IssueAsync(cancellationToken).ConfigureAwait(false);
                    failures = 0;
                    continue;
                }

                var due = await this.ComputeRenewalAsync(certificate, cancellationToken).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                this.NextRenewal = due;

                if (due <= now)
                {
                    // A certificate issued moments ago that is already "due" means the CA hands out
                    // lifetimes shorter than the check can cope with, or its renewal window is already
                    // open. Ordering again immediately would loop; wait a retry interval instead.
                    if (now - this.lastIssued < this.Options.RetryDelay)
                        wait = this.Options.RetryDelay;
                    else
                    {
                        this.logger.LogInformation(
                            "The certificate for {Names} (expires {NotAfter:u}) is due for renewal",
                            string.Join(", ", this.Identifiers.Select(x => x.Value)),
                            certificate.NotAfter
                        );

                        await this.IssueAsync(cancellationToken).ConfigureAwait(false);
                        failures = 0;
                        continue;
                    }
                }
                else
                {
                    wait = Min(due - now, Jitter(this.Options.RenewalCheckInterval, 0.1));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                wait = this.Backoff(failures, (ex as AcmeException)?.RetryAfter);

                var expiry = this.current?.NotAfter;
                this.logger.LogError(
                    ex,
                    "Obtaining a certificate for {Names} failed (attempt {Attempt}); retrying in {Delay}. {Current}",
                    string.Join(", ", this.Options.Domains),
                    failures,
                    wait,
                    expiry is { } e ? $"The current certificate stays in service until it expires at {e:u}." : "HTTPS has no certificate to serve until this succeeds."
                );
            }

            try
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    TimeSpan Backoff(int failures, TimeSpan? retryAfter)
    {
        var exponent = Math.Min(failures - 1, 20);
        var delay = TimeSpan.FromTicks(Math.Min(
            this.Options.RetryDelay.Ticks * (1L << exponent),
            this.Options.MaxRetryDelay.Ticks
        ));

        delay = Jitter(delay, 0.2);

        // A CA that says "not before" means it; retrying sooner only burns rate limit.
        return retryAfter is { } ra && ra > delay ? ra : delay;
    }

    static TimeSpan Jitter(TimeSpan value, double fraction)
    {
        var factor = 1 + ((Random.Shared.NextDouble() * 2) - 1) * fraction;
        return TimeSpan.FromTicks((long)(value.Ticks * factor));
    }

    static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    // ---- renewal timing ----

    /// <summary>
    /// When to renew. The CA's ARI window when it offers one — a random moment inside it, chosen once per
    /// window so a fleet spreads out and one server does not re-roll every check — otherwise
    /// <see cref="AcmeOptions.RenewAfterLifetimeFraction"/> of the lifetime, less a little jitter. A
    /// certificate that no longer covers the configured names is due now.
    /// </summary>
    async Task<DateTimeOffset> ComputeRenewalAsync(AcmeIssuedCertificate certificate, CancellationToken cancellationToken)
    {
        var names = AcmeCertificates.Names(certificate.Certificate);
        if (!this.Identifiers.All(x => names.Contains(x.Value)))
        {
            this.logger.LogInformation("The stored certificate does not cover every configured name; ordering a new one");
            return DateTimeOffset.MinValue;
        }

        if (this.Options.UseRenewalInfo)
        {
            try
            {
                var suggested = await this.RenewalInfoAsync(certificate, cancellationToken).ConfigureAwait(false);
                if (suggested is { } time)
                    return time;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                this.logger.LogDebug(ex, "ACME Renewal Information was unavailable; using the lifetime fraction");
            }
        }

        var lifetime = certificate.NotAfter - certificate.NotBefore;
        var at = certificate.NotBefore + lifetime * this.Options.RenewAfterLifetimeFraction;

        // Up to 2% of the lifetime earlier — a day and a bit on a 90-day certificate — so servers started
        // together do not all renew in the same minute.
        return at - lifetime * (0.02 * this.JitterSeed(certificate));
    }

    // Stable per certificate, so the renewal time does not wander between checks.
    double JitterSeed(AcmeIssuedCertificate certificate)
        => BitConverter.ToUInt16(SHA256.HashData(certificate.Certificate.RawData), 0) / (double)ushort.MaxValue;

    async Task<DateTimeOffset?> RenewalInfoAsync(AcmeIssuedCertificate certificate, CancellationToken cancellationToken)
    {
        if (AcmeCertificates.RenewalInfoId(certificate.Certificate) is not { } id)
            return null;

        var client = await this.GetClientAsync(cancellationToken).ConfigureAwait(false);
        var response = await client.GetRenewalInfoAsync(id, cancellationToken).ConfigureAwait(false);
        if (response is null)
            return null;

        var json = response.Json;
        if (!json.TryGetProperty("suggestedWindow", out var window))
            return null;

        var start = window.GetProperty("start").GetDateTimeOffset();
        var end = window.GetProperty("end").GetDateTimeOffset();

        if (json.TryGetProperty("explanationURL", out var explanation) && explanation.GetString() is { } url)
            this.logger.LogWarning("The CA has asked for early renewal of the certificate; see {Url}", url);

        if (this.renewalWindow is { } known && known.Start == start && known.End == end)
            return known.Chosen;

        var span = end - start;
        var chosen = span > TimeSpan.Zero ? start + span * Random.Shared.NextDouble() : start;
        this.renewalWindow = (start, end, chosen);

        return chosen;
    }

    // ---- issuance ----

    async Task<AcmeIssuedCertificate> IssueAsync(CancellationToken cancellationToken)
    {
        await this.issuing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var identifiers = this.Identifiers;
            var names = string.Join(", ", identifiers.Select(x => x.Value));

            for (var attempt = 0; ; attempt++)
            {
                var client = await this.GetClientAsync(cancellationToken).ConfigureAwait(false);

                // Only name the certificate being replaced to a CA that speaks ARI; to any other it is an
                // unknown field at best.
                var replaces = this.current is { } previous && client.Directory.RenewalInfo is not null && this.Options.UseRenewalInfo
                    ? AcmeCertificates.RenewalInfoId(previous.Certificate)
                    : null;

                this.logger.LogInformation("Requesting a certificate for {Names} from {Directory}", names, this.Options.DirectoryUrl);

                byte[] pkcs12;
                try
                {
                    pkcs12 = await new AcmeIssuer(client, this.Options, this.Challenges, this.logger)
                        .IssueAsync(identifiers, replaces, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (AcmeException ex) when (attempt == 0 && ex.ProblemType == "urn:ietf:params:acme:error:accountDoesNotExist")
                {
                    // The stored account was deactivated or belongs to a CA database that was reset
                    // (common with test CAs). Register afresh with the same key and go again.
                    this.logger.LogWarning("The CA no longer knows the stored account; registering again");
                    this.client = null;
                    await this.SaveAccountAsync(client.Key, null, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await this.store.SaveCertificateAsync(this.Options.DirectoryUrl, this.StorageName, pkcs12, cancellationToken).ConfigureAwait(false);

                var issued = AcmeCertificates.Load(pkcs12);
                this.lastIssued = DateTimeOffset.UtcNow;
                this.renewalWindow = null;

                this.logger.LogInformation(
                    "Issued a certificate for {Names}, valid {NotBefore:u} to {NotAfter:u} (serial {Serial})",
                    names,
                    issued.NotBefore,
                    issued.NotAfter,
                    issued.Certificate.SerialNumber
                );

                this.Install(issued, "issued");
                return issued;
            }
        }
        finally
        {
            this.issuing.Release();
        }
    }

    async Task<AcmeIssuedCertificate?> LoadStoredAsync(CancellationToken cancellationToken)
    {
        byte[]? pkcs12;
        try
        {
            pkcs12 = await this.store.LoadCertificateAsync(this.Options.DirectoryUrl, this.StorageName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.logger.LogWarning(ex, "The stored certificate could not be read; a new one will be ordered");
            return null;
        }

        if (pkcs12 is null)
            return null;

        AcmeIssuedCertificate loaded;
        try
        {
            loaded = AcmeCertificates.Load(pkcs12);
        }
        catch (Exception ex) when (ex is CryptographicException or AcmeException)
        {
            this.logger.LogWarning(ex, "The stored certificate is unreadable; a new one will be ordered");
            return null;
        }

        if (loaded.NotAfter <= DateTimeOffset.UtcNow)
        {
            this.logger.LogWarning("The stored certificate expired at {NotAfter:u}; a new one will be ordered", loaded.NotAfter);
            return null;
        }

        this.Install(loaded, "loaded from the store");
        return loaded;
    }

    void Install(AcmeIssuedCertificate certificate, string how)
    {
        var previous = this.current;
        this.current = certificate;

        this.logger.LogInformation(
            "Now serving the certificate {How} for {Subject} (expires {NotAfter:u}){Swap}",
            how,
            certificate.Certificate.Subject,
            certificate.NotAfter,
            previous is null ? "" : $", replacing the one that expires {previous.NotAfter:u} — new connections get it immediately"
        );

        try
        {
            this.CertificateChanged?.Invoke(this, certificate.Certificate);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "A {Event} handler threw", nameof(this.CertificateChanged));
        }
    }

    async Task<AcmeClient> GetClientAsync(CancellationToken cancellationToken)
    {
        if (this.client is { } existing)
            return existing;

        var directoryUrl = this.Options.DirectoryUrl;
        var stored = await this.store.LoadAccountAsync(directoryUrl, cancellationToken).ConfigureAwait(false);

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

        var client = new AcmeClient(this.http, directoryUrl, key, stored?.Location, this.logger);
        await client.LoadDirectoryAsync(cancellationToken).ConfigureAwait(false);

        var (url, created) = await client
            .EnsureAccountAsync(this.Options.Email, this.Options.AcceptTermsOfService, this.Options.ExternalAccountBinding, cancellationToken)
            .ConfigureAwait(false);

        if (url != stored?.Location)
        {
            await this.SaveAccountAsync(key, url, cancellationToken).ConfigureAwait(false);
            this.logger.LogInformation(
                created ? "Registered ACME account {Account} with {Directory}{Terms}" : "Using existing ACME account {Account} with {Directory}{Terms}",
                url,
                directoryUrl,
                client.Directory.TermsOfService is { } tos ? $"; terms of service accepted: {tos}" : ""
            );
        }

        this.client = client;
        return client;
    }

    Task SaveAccountAsync(AcmeAccountKey key, string? location, CancellationToken cancellationToken)
        => this.store.SaveAccountAsync(this.Options.DirectoryUrl, new AcmeAccountData(key.ExportPkcs8(), location), cancellationToken);
}

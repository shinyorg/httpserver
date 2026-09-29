using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Net.HttpServer.Acme.Internal;

namespace Shiny.Net.HttpServer.Acme;

/// <summary>
/// Many ACME certificates on one server, chosen per connection by SNI — hosts added, changed and
/// removed while the server runs.
/// <para>
/// Each entry is a name (yours: a site id, a host id) and the domains its certificate covers. Every
/// entry shares one ACME account, one store, one client to the CA and one challenge responder, so the
/// server answers HTTP-01 and TLS-ALPN-01 for all of them from a single middleware and a single
/// ClientHello hook, and an entry added after the server started is covered without re-wiring.
/// </para>
/// <para>
/// <b>Nothing is ordered on its own.</b> <see cref="Set"/> loads a stored certificate for the entry if
/// there is one (so restarts and imports are seamless) and never contacts the CA. The first certificate
/// comes from an explicit <see cref="IssueAsync"/> or an <see cref="ImportAsync(string, X509Certificate2, X509Certificate2Collection?, CancellationToken)"/>,
/// and a failed first issuance is not retried — CA rate limits punish a loop that keeps trying a name
/// whose DNS does not point here. Once an entry has a certificate, renewal is automatic: the same
/// schedule, ACME Renewal Information and backoff as <see cref="AcmeCertificateManager"/>.
/// </para>
/// <code>
/// var acme = new AcmeCertificateRegistry(new AcmeRegistryOptions
/// {
///     Email = "ops@example.com",
///     AcceptTermsOfService = true,
///     DirectoryUrl = AcmeDirectories.LetsEncryptStaging
/// });
///
/// server.UseAcme(acme);        // one HTTP-01 middleware, every HTTPS endpoint without a certificate
/// http3.UseAcme(acme);         // same selector on QUIC
///
/// acme.Set("grafana", ["grafana.example.com"]);
/// await acme.IssueAsync("grafana", ct);       // explicit; throws AcmeException on failure
/// </code>
/// </summary>
public sealed class AcmeCertificateRegistry : IAsyncDisposable
{
    static readonly IReadOnlyDictionary<string, Entry> EmptyIndex = new Dictionary<string, Entry>();

    readonly ILoggerFactory loggerFactory;
    readonly ILogger logger;
    readonly AcmeAccountContext account;
    readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    readonly object gate = new();
    readonly SslStreamCertificateContext? fallback;

    volatile IReadOnlyDictionary<string, Entry> index = EmptyIndex;
    volatile bool renewing;
    int disposed;

    public AcmeCertificateRegistry(AcmeRegistryOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        this.Options = options;
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        this.logger = this.loggerFactory.CreateLogger<AcmeCertificateRegistry>();
        this.account = AcmeAccountContext.From(options.ToOptions([]));

        if (options.FallbackCertificate is { } certificate)
        {
            if (!certificate.HasPrivateKey)
                throw new ArgumentException("The fallback certificate has no private key.", nameof(options));

            this.fallback = SslStreamCertificateContext.Create(certificate, null, offline: true);
        }
    }

    /// <summary>The options this registry was created with.</summary>
    public AcmeRegistryOptions Options { get; }

    /// <summary>
    /// Raised after an entry's certificate is installed — loaded from the store, issued, renewed or
    /// imported. The server is already serving it when this fires. Handlers run on the thread that
    /// installed it; keep them short.
    /// </summary>
    public event EventHandler<AcmeCertificateEventArgs>? CertificateChanged;

    /// <summary>
    /// Raised when an order for an entry fails: an explicit <see cref="IssueAsync"/> or
    /// <see cref="RenewAsync"/> (which also throw), or a background renewal (which backs off and retries
    /// while the current certificate keeps serving).
    /// </summary>
    public event EventHandler<AcmeIssuanceFailedEventArgs>? IssuanceFailed;

    internal AcmeChallengeResponder Challenges => this.account.Challenges;

    /// <summary>Every entry, with what it is serving and how its last order went. A snapshot.</summary>
    public IReadOnlyList<AcmeRegistryEntry> Entries
        => [.. this.entries.Values.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Snapshot())];

    /// <summary>One entry's status, or null when there is no entry by that name.</summary>
    public AcmeRegistryEntry? GetEntry(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return this.entries.TryGetValue(name, out var entry) ? entry.Snapshot() : null;
    }

    /// <summary>
    /// Adds an entry, or replaces the domains of an existing one. Never contacts the CA.
    /// <para>
    /// A new entry loads the certificate stored for exactly these domains, if there is one, in the
    /// background (<see cref="LoadAsync"/> awaits it). Changing an existing entry's domains keeps its
    /// current certificate serving; the next <see cref="IssueAsync"/> or scheduled renewal orders one
    /// covering the new list — unless a certificate for the new list is already stored, which is then
    /// loaded and served. Setting the same domains again does nothing.
    /// </para>
    /// </summary>
    /// <exception cref="AcmeException">A domain is empty, invalid or a wildcard.</exception>
    public void Set(string name, IEnumerable<string> domains)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(domains);
        ObjectDisposedException.ThrowIf(this.disposed != 0, this);

        var identifiers = AcmeIdentifier.Parse(domains);
        var values = identifiers.Select(x => x.Value).ToArray();

        Entry? previous;
        Entry entry;

        lock (this.gate)
        {
            if (this.entries.TryGetValue(name, out previous) && SameNames(previous.Domains, values))
                return;

            var manager = new AcmeCertificateManager(
                this.Options.ToOptions(values),
                this.loggerFactory,
                this.account,
                ownsAccount: false,
                issueWhenMissing: false,
                name
            );

            entry = new Entry(name, values, manager);

            if (previous?.Manager.Current is { } serving)
                manager.Adopt(serving);

            manager.CertificateChanged += (_, certificate) => this.OnCertificateChanged(entry, certificate);
            manager.IssuanceFailed += (_, exception) => this.OnIssuanceFailed(entry, exception);

            this.entries[name] = entry;
            this.RebuildIndex();

            entry.Loading = this.LoadEntryAsync(entry);
        }

        if (previous is not null)
            _ = this.RetireAsync(previous);

        if (this.renewing && entry.Manager.Current is not null)
            entry.Manager.StartRenewal();
    }

    /// <summary>
    /// Removes an entry: its certificate stops being served at once and its renewal stops. The stored
    /// certificate is kept, so setting the entry again within its lifetime serves it again without an
    /// order. Returns false when there was no such entry.
    /// </summary>
    public async Task<bool> RemoveAsync(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        Entry? removed;
        lock (this.gate)
        {
            if (!this.entries.TryRemove(name, out removed))
                return false;

            this.RebuildIndex();
        }

        await this.RetireAsync(removed).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Awaits the entry's initial load from the store, and returns the certificate it is serving — null
    /// when nothing was stored for it.
    /// </summary>
    public async Task<X509Certificate2?> LoadAsync(string name, CancellationToken cancellationToken = default)
    {
        var entry = this.Find(name);
        await entry.Loading.WaitAsync(cancellationToken).ConfigureAwait(false);
        return entry.Manager.Certificate;
    }

    /// <summary>
    /// Orders a certificate for the entry now and serves it; from then on it renews by itself. The
    /// only way an entry gets its first certificate from the CA. Also re-orders an entry that already has
    /// one — after its domains changed, say.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No entry has that name.</exception>
    /// <exception cref="AcmeException">The order failed; <see cref="AcmeException.ProblemType"/> and
    /// <see cref="AcmeException.Detail"/> carry the CA's reason. Not retried.</exception>
    public async Task<X509Certificate2> IssueAsync(string name, CancellationToken cancellationToken = default)
    {
        var entry = this.Find(name);
        await entry.Loading.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await entry.Manager.RenewAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not AcmeException and not ObjectDisposedException
            && (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            throw new AcmeException($"Issuing the certificate for '{name}' failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Replaces an issued certificate now, whatever its age — for a key compromise or a CA's revocation
    /// notice. For an entry with no certificate yet, use <see cref="IssueAsync"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The entry has no certificate to renew.</exception>
    public Task<X509Certificate2> RenewAsync(string name, CancellationToken cancellationToken = default)
    {
        var entry = this.Find(name);
        if (entry.Manager.Current is null && entry.Loading.IsCompleted)
            throw new InvalidOperationException($"'{name}' has no certificate to renew. Use {nameof(this.IssueAsync)} for its first one.");

        return this.IssueAsync(name, cancellationToken);
    }

    /// <summary>
    /// Adopts a certificate obtained elsewhere — another ACME client, a migration — for an entry. It is
    /// saved to the store under the entry's storage name and served at once, and from then on renews on
    /// this registry's schedule, restarts included. No order is placed.
    /// </summary>
    /// <param name="name">An existing entry (<see cref="Set"/> it first).</param>
    /// <param name="certificate">The certificate, with its private key.</param>
    /// <param name="chain">Intermediates to send with it; null when there are none to add.</param>
    /// <param name="cancellationToken">Cancels the store write.</param>
    public async Task<X509Certificate2> ImportAsync(
        string name,
        X509Certificate2 certificate,
        X509Certificate2Collection? chain = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(certificate);

        if (!certificate.HasPrivateKey)
            throw new AcmeException("The certificate being imported has no private key.");

        var bundle = new X509Certificate2Collection { certificate };
        if (chain is not null)
        {
            foreach (var intermediate in chain)
            {
                if (intermediate.Thumbprint != certificate.Thumbprint)
                    bundle.Add(intermediate);
            }
        }

        byte[] pkcs12;
        try
        {
            pkcs12 = bundle.Export(X509ContentType.Pkcs12)
                ?? throw new AcmeException("Exporting the certificate being imported failed.");
        }
        catch (CryptographicException ex)
        {
            throw new AcmeException("The certificate being imported could not be exported with its key. Load it with X509KeyStorageFlags.Exportable.", ex);
        }

        return await this.ImportAsync(name, pkcs12, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adopts a PEM certificate — <c>fullchain.pem</c> and <c>privkey.pem</c> as certbot and most ACME
    /// clients write them. See <see cref="ImportAsync(string, X509Certificate2, X509Certificate2Collection?, CancellationToken)"/>.
    /// </summary>
    /// <param name="name">An existing entry.</param>
    /// <param name="fullChainPem">The certificate first, then its intermediates.</param>
    /// <param name="privateKeyPem">The certificate's private key (PKCS#8, or PKCS#1 / SEC1).</param>
    /// <param name="cancellationToken">Cancels the store write.</param>
    public Task<X509Certificate2> ImportAsync(
        string name,
        string fullChainPem,
        string privateKeyPem,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullChainPem);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);

        X509Certificate2 leaf;
        var chain = new X509Certificate2Collection();
        try
        {
            leaf = X509Certificate2.CreateFromPem(fullChainPem, privateKeyPem);
            chain.ImportFromPem(fullChainPem);
        }
        catch (CryptographicException ex)
        {
            throw new AcmeException("The PEM certificate or key could not be read.", ex);
        }

        return this.ImportAsync(name, leaf, chain, cancellationToken);
    }

    /// <summary>
    /// Adopts a PKCS#12 (<c>.pfx</c>) certificate with its key and chain. See
    /// <see cref="ImportAsync(string, X509Certificate2, X509Certificate2Collection?, CancellationToken)"/>.
    /// </summary>
    public async Task<X509Certificate2> ImportAsync(
        string name,
        byte[] pkcs12,
        string? password,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(pkcs12);

        var entry = this.Find(name);
        await entry.Loading.WaitAsync(cancellationToken).ConfigureAwait(false);

        // The store holds password-less PKCS#12, as every certificate this package issues is saved.
        if (password is not null)
        {
            try
            {
                var collection = X509CertificateLoader.LoadPkcs12Collection(pkcs12, password, X509KeyStorageFlags.Exportable);
                pkcs12 = collection.Export(X509ContentType.Pkcs12)
                    ?? throw new AcmeException("Re-exporting the certificate being imported failed.");
            }
            catch (CryptographicException ex)
            {
                throw new AcmeException("The PKCS#12 certificate could not be read. Is the password right?", ex);
            }
        }

        try
        {
            var imported = await entry.Manager.ImportAsync(pkcs12, cancellationToken).ConfigureAwait(false);
            this.logger.LogInformation("Imported a certificate for '{Name}', expiring {NotAfter:u}", name, imported.NotAfter);
            return imported.Certificate;
        }
        catch (CryptographicException ex)
        {
            throw new AcmeException("The certificate being imported could not be read.", ex);
        }
    }

    /// <summary>
    /// The selector every wired endpoint calls per handshake. An entry whose domains or certificate
    /// names include <paramref name="serverName"/> exactly (case-insensitive); then a <c>*.</c> wildcard
    /// on a served certificate covering it; then <see cref="AcmeRegistryOptions.FallbackCertificate"/>;
    /// otherwise null, which refuses the handshake.
    /// </summary>
    public SslStreamCertificateContext? SelectCertificate(string? serverName)
    {
        if (serverName is { Length: > 0 })
        {
            var index = this.index;
            var host = serverName[^1] == '.' ? serverName[..^1] : serverName;

            if (index.TryGetValue(host, out var exact) && exact.Manager.CertificateContext is { } context)
                return context;

            var dot = host.IndexOf('.');
            if (dot > 0 && index.TryGetValue(string.Concat("*", host.AsSpan(dot)), out var wildcard) && wildcard.Manager.CertificateContext is { } wild)
                return wild;
        }

        return this.fallback;
    }

    /// <summary>
    /// Starts renewing every entry that has a certificate, and every one that gets one from now on.
    /// Idempotent. <c>UseAcme</c> calls this when the server reaches Running.
    /// </summary>
    public void StartRenewal()
    {
        ObjectDisposedException.ThrowIf(this.disposed != 0, this);
        this.renewing = true;

        foreach (var entry in this.entries.Values)
        {
            if (entry.Manager.Current is not null)
                TryStart(entry.Manager);
        }
    }

    /// <summary>Stops every entry's renewal loop, waiting for orders in flight to notice. Idempotent.</summary>
    public async Task StopRenewalAsync()
    {
        this.renewing = false;
        await Task.WhenAll(this.entries.Values.Select(x => x.Manager.StopRenewalAsync())).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        this.renewing = false;

        var all = this.entries.Values.ToArray();
        this.entries.Clear();
        this.index = EmptyIndex;

        foreach (var entry in all)
            await entry.Manager.DisposeAsync().ConfigureAwait(false);

        this.account.Dispose();
    }

    // ---- internals ----

    Entry Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectDisposedException.ThrowIf(this.disposed != 0, this);

        return this.entries.TryGetValue(name, out var entry)
            ? entry
            : throw new KeyNotFoundException($"No ACME registry entry is named '{name}'. Call {nameof(this.Set)} first.");
    }

    async Task LoadEntryAsync(Entry entry)
    {
        // Off the caller's thread: Set is synchronous, and the store may be a database.
        await Task.Yield();

        try
        {
            await entry.Manager.LoadAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Loading the stored certificate for '{Name}' failed", entry.Name);
        }
    }

    async Task RetireAsync(Entry entry)
    {
        try
        {
            await entry.Manager.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Stopping the renewal of '{Name}' failed", entry.Name);
        }
    }

    bool IsCurrent(Entry entry) => this.entries.TryGetValue(entry.Name, out var current) && ReferenceEquals(current, entry);

    void OnCertificateChanged(Entry entry, X509Certificate2 certificate)
    {
        if (!this.IsCurrent(entry))
            return;

        entry.LastError = null;

        lock (this.gate)
            this.RebuildIndex();

        if (this.renewing)
            TryStart(entry.Manager);

        try
        {
            this.CertificateChanged?.Invoke(this, new AcmeCertificateEventArgs(entry.Name, certificate));
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "A {Event} handler threw", nameof(this.CertificateChanged));
        }
    }

    void OnIssuanceFailed(Entry entry, Exception exception)
    {
        if (!this.IsCurrent(entry))
            return;

        entry.LastError = exception;
        entry.LastErrorAt = DateTimeOffset.UtcNow;

        try
        {
            this.IssuanceFailed?.Invoke(this, new AcmeIssuanceFailedEventArgs(entry.Name, exception, entry.Manager.Current is not null));
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "A {Event} handler threw", nameof(this.IssuanceFailed));
        }
    }

    static void TryStart(AcmeCertificateManager manager)
    {
        try
        {
            manager.StartRenewal();
        }
        catch (ObjectDisposedException)
        {
            // Replaced or removed a moment ago.
        }
    }

    /// <summary>
    /// Rebuilds the SNI map and swaps it in whole, so a handshake reads one consistent snapshot without
    /// a lock. Configured domains first, then every name on the served certificates (wildcards included);
    /// the first claim on a name wins, entries taken in name order so the winner does not wander.
    /// Called under <see cref="gate"/>.
    /// </summary>
    void RebuildIndex()
    {
        var map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var ordered = this.entries.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();

        foreach (var entry in ordered)
        {
            foreach (var domain in entry.Domains)
                map.TryAdd(domain, entry);
        }

        foreach (var entry in ordered)
        {
            if (entry.Manager.Certificate is { } certificate)
            {
                foreach (var name in AcmeCertificates.Names(certificate))
                    map.TryAdd(name, entry);
            }
        }

        this.index = map;
    }

    static bool SameNames(IReadOnlyList<string> a, IReadOnlyList<string> b)
        => a.Count == b.Count && a.Order(StringComparer.Ordinal).SequenceEqual(b.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    sealed class Entry(string name, IReadOnlyList<string> domains, AcmeCertificateManager manager)
    {
        public string Name { get; } = name;
        public IReadOnlyList<string> Domains { get; } = domains;
        public AcmeCertificateManager Manager { get; } = manager;
        public Task Loading { get; set; } = Task.CompletedTask;
        public volatile Exception? LastError;
        public DateTimeOffset? LastErrorAt { get; set; }

        public AcmeRegistryEntry Snapshot()
        {
            var manager = this.Manager;
            var certificate = manager.Certificate;
            var error = this.LastError;

            var state = manager.IsIssuing
                ? AcmeCertificateState.Issuing
                : certificate is not null
                    ? AcmeCertificateState.Issued
                    : error is not null ? AcmeCertificateState.Failed : AcmeCertificateState.None;

            return new AcmeRegistryEntry(
                this.Name,
                this.Domains,
                state,
                certificate,
                manager.CurrentNotAfter,
                certificate is null ? null : manager.NextRenewal,
                error,
                this.LastErrorAt
            );
        }
    }
}

/// <summary>Where an <see cref="AcmeCertificateRegistry"/> entry stands.</summary>
public enum AcmeCertificateState
{
    /// <summary>No certificate, and none has been asked for (or the last attempt's error was cleared).</summary>
    None,

    /// <summary>An order is in flight — a first issuance or a renewal.</summary>
    Issuing,

    /// <summary>A certificate is being served. A failed renewal leaves this state, with <see cref="AcmeRegistryEntry.LastError"/> set.</summary>
    Issued,

    /// <summary>No certificate, and the last order failed. Stays so until someone calls <see cref="AcmeCertificateRegistry.IssueAsync"/> again.</summary>
    Failed
}

/// <summary>One <see cref="AcmeCertificateRegistry"/> entry, as it was when read.</summary>
/// <param name="Name">The entry's name.</param>
/// <param name="Domains">The names its certificate is ordered for, normalized (lower case, IDNs as A-labels).</param>
/// <param name="State">Where it stands.</param>
/// <param name="Certificate">The certificate being served, or null.</param>
/// <param name="NotAfter">When that certificate expires.</param>
/// <param name="NextRenewal">When the renewal loop next intends to renew, once it has looked; null before.</param>
/// <param name="LastError">Why the last order failed; cleared when a certificate is installed.</param>
/// <param name="LastErrorAt">When it failed.</param>
public sealed record AcmeRegistryEntry(
    string Name,
    IReadOnlyList<string> Domains,
    AcmeCertificateState State,
    X509Certificate2? Certificate,
    DateTimeOffset? NotAfter,
    DateTimeOffset? NextRenewal,
    Exception? LastError,
    DateTimeOffset? LastErrorAt
);

/// <summary>An <see cref="AcmeCertificateRegistry"/> entry's certificate changed.</summary>
public sealed class AcmeCertificateEventArgs(string name, X509Certificate2 certificate) : EventArgs
{
    /// <summary>The entry.</summary>
    public string Name { get; } = name;

    /// <summary>The certificate now being served.</summary>
    public X509Certificate2 Certificate { get; } = certificate;
}

/// <summary>An order for an <see cref="AcmeCertificateRegistry"/> entry failed.</summary>
public sealed class AcmeIssuanceFailedEventArgs(string name, Exception exception, bool isRenewal) : EventArgs
{
    /// <summary>The entry.</summary>
    public string Name { get; } = name;

    /// <summary>Why — usually an <see cref="AcmeException"/> carrying the CA's problem document.</summary>
    public Exception Exception { get; } = exception;

    /// <summary>
    /// True when the entry already had a certificate, which keeps serving and whose renewal is retried
    /// with backoff. False for a first issuance, which is not retried.
    /// </summary>
    public bool IsRenewal { get; } = isRenewal;
}

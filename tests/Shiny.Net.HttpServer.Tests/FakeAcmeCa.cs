using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// An ACME CA (RFC 8555) small enough to read, strict enough to catch a client that is merely
/// self-consistent: it verifies every JWS signature against the account key, consumes nonces, checks
/// the <c>url</c> header against the request, verifies External Account Binding MACs, and really
/// performs HTTP-01 and TLS-ALPN-01 against the server under test before it issues anything.
/// <para>
/// Hosted on this same HttpServer, on loopback, over plain HTTP — so the tests need no internet, no
/// DNS and no trust store. Names are "resolved" to loopback ports the test supplies.
/// </para>
/// </summary>
sealed class FakeAcmeCa : IAsyncDisposable
{
    readonly HttpServer server;
    readonly ConcurrentDictionary<string, byte> nonces = new();
    readonly ConcurrentDictionary<string, Account> accounts = new();
    readonly ConcurrentDictionary<string, Order> orders = new();
    readonly ConcurrentDictionary<string, Authorization> authorizations = new();
    readonly ConcurrentDictionary<string, Challenge> challenges = new();
    readonly ConcurrentDictionary<string, string> certificates = new();
    readonly X509Certificate2 root;
    readonly X509Certificate2 intermediate;
    int ids;
    int rejectNonces;

    FakeAcmeCa(HttpServer server)
    {
        this.server = server;
        (this.root, this.intermediate) = CreateHierarchy();
    }

    public string BaseUrl { get; private set; } = "";
    public string DirectoryUrl => this.BaseUrl + "/directory";

    public X509Certificate2 Root => this.root;
    public X509Certificate2 Intermediate => this.intermediate;

    /// <summary>Where "the internet" finds the server under test: port 80 and port 443 stand-ins.</summary>
    public Func<int>? HttpPort { get; set; }
    public Func<int>? TlsPort { get; set; }

    public string[] OfferedChallenges { get; set; } = ["http-01", "tls-alpn-01"];

    /// <summary>EAB credentials this CA demands, or null for none.</summary>
    public (string KeyId, byte[] Key)? RequireEab { get; set; }

    /// <summary>How far the next certificates' notBefore is pushed into the past, one per issuance.</summary>
    public ConcurrentQueue<TimeSpan> Backdates { get; } = new();

    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(90);

    /// <summary>ARI window to report, or null to leave renewalInfo out of the directory.</summary>
    public Func<(DateTimeOffset Start, DateTimeOffset End)>? RenewalWindow { get; set; }

    public int BadNoncesSent;
    public int CertificatesIssued;
    public string? LastReplaces;
    public int AccountsCreated => this.accounts.Count;
    public ConcurrentQueue<string> Validations { get; } = new();
    public List<X509Certificate2> Issued { get; } = [];

    /// <summary>Makes the next <paramref name="count"/> signed requests fail with badNonce.</summary>
    public void RejectNextNonces(int count) => Interlocked.Exchange(ref this.rejectNonces, count);

    public static async Task<FakeAcmeCa> StartAsync()
    {
        var builder = HttpServer.CreateBuilder();
        builder.Options.Address = IPAddress.Loopback;
        builder.Options.Port = 0;
        builder.Options.HideExceptionDetails = false;

        var server = builder.Build();
        var ca = new FakeAcmeCa(server);
        ca.Map();

        await server.StartAsync();
        ca.BaseUrl = server.ListenUrl!.TrimEnd('/');

        return ca;
    }

    public async ValueTask DisposeAsync() => await this.server.DisposeAsync();

    void Map()
    {
        this.server.MapGet("/directory", ctx => this.Directory(ctx));
        this.server.Map(HttpMethods.Head, "/new-nonce", ctx => this.Empty(ctx, 200));
        this.server.MapGet("/new-nonce", ctx => this.Empty(ctx, 204));
        this.server.MapPost("/new-account", ctx => this.NewAccount(ctx));
        this.server.MapPost("/new-order", ctx => this.NewOrder(ctx));
        this.server.MapPost("/authz/{id}", ctx => this.GetAuthorization(ctx));
        this.server.MapPost("/chall/{id}", ctx => this.RespondToChallenge(ctx));
        this.server.MapPost("/order/{id}", ctx => this.GetOrder(ctx));
        this.server.MapPost("/finalize/{id}", ctx => this.Finalize(ctx));
        this.server.MapPost("/cert/{id}", ctx => this.DownloadCertificate(ctx));
        this.server.MapGet("/renewal-info/{id}", ctx => this.RenewalInfo(ctx));
    }

    // ---- resources ----

    async ValueTask Directory(HttpContext ctx)
    {
        await Json(ctx, 200, w =>
        {
            w.WriteStartObject();
            w.WriteString("newNonce", this.BaseUrl + "/new-nonce");
            w.WriteString("newAccount", this.BaseUrl + "/new-account");
            w.WriteString("newOrder", this.BaseUrl + "/new-order");
            w.WriteString("revokeCert", this.BaseUrl + "/revoke-cert");
            w.WriteString("keyChange", this.BaseUrl + "/key-change");
            if (this.RenewalWindow is not null)
                w.WriteString("renewalInfo", this.BaseUrl + "/renewal-info");
            w.WriteStartObject("meta");
            w.WriteString("termsOfService", "https://fake-ca.test/terms");
            if (this.RequireEab is not null)
                w.WriteBoolean("externalAccountRequired", true);
            w.WriteEndObject();
            w.WriteEndObject();
        });
    }

    async ValueTask NewAccount(HttpContext ctx)
    {
        var request = await this.VerifyAsync(ctx, allowJwk: true);
        if (request is null)
            return;

        var payload = request.Payload!.Value;
        if (!payload.TryGetProperty("termsOfServiceAgreed", out var tos) || tos.ValueKind != JsonValueKind.True)
        {
            await this.Problem(ctx, 403, "userActionRequired", "Terms of service must be agreed.");
            return;
        }

        var existing = this.accounts.Values.FirstOrDefault(x => x.Thumbprint == request.Thumbprint);
        if (existing is not null)
        {
            ctx.Response.Headers["Location"] = existing.Url;
            await this.AccountBody(ctx, 200);
            return;
        }

        if (this.RequireEab is { } eab)
        {
            if (!payload.TryGetProperty("externalAccountBinding", out var binding))
            {
                await this.Problem(ctx, 400, "externalAccountRequired", "This CA requires external account binding.");
                return;
            }

            var error = VerifyEab(binding, eab, request.Jwk!.Value, this.BaseUrl + "/new-account");
            if (error is not null)
            {
                await this.Problem(ctx, 401, "unauthorized", error);
                return;
            }
        }

        var id = this.NextId();
        var account = new Account(this.BaseUrl + "/acct/" + id, request.Key!, request.Thumbprint!);
        this.accounts[account.Url] = account;

        ctx.Response.Headers["Location"] = account.Url;
        await this.AccountBody(ctx, 201);
    }

    ValueTask AccountBody(HttpContext ctx, int status) => Json(ctx, status, w =>
    {
        w.WriteStartObject();
        w.WriteString("status", "valid");
        w.WriteEndObject();
    });

    async ValueTask NewOrder(HttpContext ctx)
    {
        var request = await this.VerifyAsync(ctx);
        if (request is null)
            return;

        var payload = request.Payload!.Value;
        if (payload.TryGetProperty("replaces", out var replaces))
            this.LastReplaces = replaces.GetString();

        var order = new Order(this.NextId(), request.Account!);

        foreach (var identifier in payload.GetProperty("identifiers").EnumerateArray())
        {
            var authorization = new Authorization(
                this.NextId(),
                identifier.GetProperty("type").GetString()!,
                identifier.GetProperty("value").GetString()!,
                order
            );

            foreach (var type in this.OfferedChallenges)
            {
                var challenge = new Challenge(this.NextId(), type, Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)), authorization);
                authorization.Challenges.Add(challenge);
                this.challenges[challenge.Id] = challenge;
            }

            order.Authorizations.Add(authorization);
            this.authorizations[authorization.Id] = authorization;
        }

        this.orders[order.Id] = order;

        ctx.Response.Headers["Location"] = this.BaseUrl + "/order/" + order.Id;
        await this.OrderBody(ctx, 201, order);
    }

    async ValueTask GetOrder(HttpContext ctx)
    {
        var request = await this.VerifyAsync(ctx);
        if (request is null || !this.orders.TryGetValue(ctx.Request.RouteValues["id"]!, out var order))
            return;

        // "processing" is visible for exactly one poll, so the client's polling is exercised.
        var body = this.OrderBody(ctx, 200, order);
        if (order.Status == "processing")
            order.Status = "valid";

        await body;
    }

    ValueTask OrderBody(HttpContext ctx, int status, Order order)
    {
        var snapshot = order.Status;
        return Json(ctx, status, w =>
        {
            w.WriteStartObject();
            w.WriteString("status", snapshot);
            w.WriteStartArray("identifiers");
            foreach (var a in order.Authorizations)
            {
                w.WriteStartObject();
                w.WriteString("type", a.Type);
                w.WriteString("value", a.Value);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("authorizations");
            foreach (var a in order.Authorizations)
                w.WriteStringValue(this.BaseUrl + "/authz/" + a.Id);
            w.WriteEndArray();
            w.WriteString("finalize", this.BaseUrl + "/finalize/" + order.Id);
            if (snapshot == "valid")
                w.WriteString("certificate", this.BaseUrl + "/cert/" + order.Id);
            w.WriteEndObject();
        });
    }

    async ValueTask GetAuthorization(HttpContext ctx)
    {
        var request = await this.VerifyAsync(ctx);
        if (request is null || !this.authorizations.TryGetValue(ctx.Request.RouteValues["id"]!, out var authorization))
            return;

        await Json(ctx, 200, w =>
        {
            w.WriteStartObject();
            w.WriteString("status", authorization.Status);
            w.WriteStartObject("identifier");
            w.WriteString("type", authorization.Type);
            w.WriteString("value", authorization.Value);
            w.WriteEndObject();
            w.WriteStartArray("challenges");
            foreach (var c in authorization.Challenges)
                this.WriteChallenge(w, c);
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    void WriteChallenge(Utf8JsonWriter w, Challenge c)
    {
        w.WriteStartObject();
        w.WriteString("type", c.Type);
        w.WriteString("url", this.BaseUrl + "/chall/" + c.Id);
        w.WriteString("token", c.Token);
        w.WriteString("status", c.Status);
        if (c.Error is not null)
        {
            w.WriteStartObject("error");
            w.WriteString("type", "urn:ietf:params:acme:error:unauthorized");
            w.WriteString("detail", c.Error);
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    async ValueTask RespondToChallenge(HttpContext ctx)
    {
        var request = await this.VerifyAsync(ctx);
        if (request is null || !this.challenges.TryGetValue(ctx.Request.RouteValues["id"]!, out var challenge))
            return;

        if (challenge.Status == "pending")
        {
            challenge.Status = "processing";
            var keyAuthorization = challenge.Token + "." + request.Account!.Thumbprint;
            _ = Task.Run(() => this.ValidateAsync(challenge, keyAuthorization));
        }

        await Json(ctx, 200, w => this.WriteChallenge(w, challenge));
    }

    async Task ValidateAsync(Challenge challenge, string keyAuthorization)
    {
        var authorization = challenge.Authorization;
        string? error;

        try
        {
            error = challenge.Type == "http-01"
                ? await this.ValidateHttpAsync(authorization.Value, challenge.Token, keyAuthorization)
                : await this.ValidateTlsAlpnAsync(authorization.Value, keyAuthorization);
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
        }

        this.Validations.Enqueue($"{challenge.Type} {authorization.Value} {(error is null ? "ok" : error)}");

        challenge.Error = error;
        challenge.Status = error is null ? "valid" : "invalid";
        authorization.Status = challenge.Status;

        var order = authorization.Order;
        if (order.Authorizations.Any(x => x.Status == "invalid"))
            order.Status = "invalid";
        else if (order.Authorizations.All(x => x.Status == "valid"))
            order.Status = "ready";
    }

    async Task<string?> ValidateHttpAsync(string name, string token, string keyAuthorization)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{this.HttpPort!()}/.well-known/acme-challenge/{token}");
        request.Headers.Host = name;

        using var response = await client.SendAsync(request);
        var body = (await response.Content.ReadAsStringAsync()).Trim();

        if (response.StatusCode != HttpStatusCode.OK)
            return $"http-01 fetch for {name} returned {(int)response.StatusCode}";

        return body == keyAuthorization ? null : $"http-01 for {name} returned the wrong key authorization";
    }

    async Task<string?> ValidateTlsAlpnAsync(string name, string keyAuthorization)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, this.TlsPort!());

        X509Certificate2? presented = null;
        await using var ssl = new SslStream(tcp.GetStream());
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = name,
            ApplicationProtocols = [new SslApplicationProtocol("acme-tls/1")],
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                presented = certificate is null ? null : new X509Certificate2(certificate);
                return true;
            }
        });

        if (ssl.NegotiatedApplicationProtocol.ToString() != "acme-tls/1")
            return $"tls-alpn-01 for {name} negotiated '{ssl.NegotiatedApplicationProtocol}' instead of acme-tls/1";

        if (presented is null)
            return "tls-alpn-01 presented no certificate";

        var san = presented.Extensions.OfType<X509SubjectAlternativeNameExtension>().SingleOrDefault();
        if (san is null || san.EnumerateDnsNames().Single() != name)
            return "tls-alpn-01 certificate does not name exactly " + name;

        var extension = presented.Extensions["1.3.6.1.5.5.7.1.31"];
        if (extension is null || !extension.Critical)
            return "tls-alpn-01 certificate lacks a critical acmeIdentifier extension";

        var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
        var digest = reader.ReadOctetString();
        return digest.AsSpan().SequenceEqual(SHA256.HashData(Encoding.ASCII.GetBytes(keyAuthorization)))
            ? null
            : "tls-alpn-01 acmeIdentifier does not match the key authorization";
    }

    async ValueTask Finalize(HttpContext ctx)
    {
        var request = await this.VerifyAsync(ctx);
        if (request is null || !this.orders.TryGetValue(ctx.Request.RouteValues["id"]!, out var order))
            return;

        if (order.Status != "ready")
        {
            await this.Problem(ctx, 403, "orderNotReady", $"Order is {order.Status}.");
            return;
        }

        var csr = Base64Url.DecodeFromChars(request.Payload!.Value.GetProperty("csr").GetString()!);
        var certificateRequest = CertificateRequest.LoadSigningRequest(
            csr,
            HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions
        );

        var san = certificateRequest.CertificateExtensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        var requested = san.EnumerateDnsNames().Concat(san.EnumerateIPAddresses().Select(x => x.ToString())).Order().ToArray();
        var ordered = order.Authorizations.Select(x => x.Value).Order().ToArray();
        if (!requested.SequenceEqual(ordered))
        {
            await this.Problem(ctx, 400, "badCSR", "CSR names do not match the order.");
            return;
        }

        this.Backdates.TryDequeue(out var backdate);
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5) - backdate;

        certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        certificateRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        certificateRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(this.intermediate, true, false));

        // The signature-generator overload, because the subject key may be RSA while the issuer is ECDSA.
        using var issuerKey = this.intermediate.GetECDsaPrivateKey()!;
        using var leaf = certificateRequest.Create(
            this.intermediate.SubjectName,
            X509SignatureGenerator.CreateForECDsa(issuerKey),
            notBefore,
            notBefore + this.Lifetime,
            RandomNumberGenerator.GetBytes(16)
        );

        lock (this.Issued)
            this.Issued.Add(X509CertificateLoader.LoadCertificate(leaf.RawData));

        this.certificates[order.Id] = leaf.ExportCertificatePem() + "\n" + this.intermediate.ExportCertificatePem() + "\n";
        Interlocked.Increment(ref this.CertificatesIssued);

        order.Status = "processing";
        await this.OrderBody(ctx, 200, order);
    }

    async ValueTask DownloadCertificate(HttpContext ctx)
    {
        var request = await this.VerifyAsync(ctx);
        if (request is null || !this.certificates.TryGetValue(ctx.Request.RouteValues["id"]!, out var pem))
            return;

        this.AddNonce(ctx);
        await ctx.Response.WriteTextAsync(pem, "application/pem-certificate-chain");
    }

    async ValueTask RenewalInfo(HttpContext ctx)
    {
        var (start, end) = this.RenewalWindow!();
        await Json(ctx, 200, w =>
        {
            w.WriteStartObject();
            w.WriteStartObject("suggestedWindow");
            w.WriteString("start", start);
            w.WriteString("end", end);
            w.WriteEndObject();
            w.WriteEndObject();
        });
    }

    /// <summary>The ARI certificate id this CA expects for one of its certificates.</summary>
    public static string RenewalInfoId(X509Certificate2 certificate)
    {
        var aki = certificate.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().Single();
        return Base64Url.EncodeToString(aki.KeyIdentifier!.Value.Span) + "." + Base64Url.EncodeToString(certificate.SerialNumberBytes.Span);
    }

    // ---- JWS ----

    sealed record Verified(JsonElement? Payload, Account? Account, ECDsa? Key, string? Thumbprint, JsonElement? Jwk);

    /// <summary>
    /// RFC 8555 §6.2–6.5: flattened JWS, ES256, a nonce this CA issued and has not seen used, a url
    /// header naming this exact resource, and either a jwk (newAccount only) or the kid of a known account.
    /// </summary>
    async Task<Verified?> VerifyAsync(HttpContext ctx, bool allowJwk = false)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement;

        var protectedPart = body.GetProperty("protected").GetString()!;
        var payloadPart = body.GetProperty("payload").GetString()!;
        var signature = Base64Url.DecodeFromChars(body.GetProperty("signature").GetString()!);
        var header = JsonDocument.Parse(Base64Url.DecodeFromChars(protectedPart)).RootElement;

        if (ctx.Request.ContentType != "application/jose+json")
        {
            await this.Problem(ctx, 415, "malformed", "Content-Type must be application/jose+json.");
            return null;
        }

        if (header.GetProperty("alg").GetString() != "ES256")
        {
            await this.Problem(ctx, 400, "badSignatureAlgorithm", "ES256 only.");
            return null;
        }

        var nonce = header.GetProperty("nonce").GetString()!;
        if (!this.nonces.TryRemove(nonce, out _) || Interlocked.Decrement(ref this.rejectNonces) >= 0)
        {
            Interlocked.Increment(ref this.BadNoncesSent);
            await this.Problem(ctx, 400, "badNonce", "Stale nonce.");
            return null;
        }

        if (header.GetProperty("url").GetString() != this.BaseUrl + ctx.Request.Path)
        {
            await this.Problem(ctx, 401, "unauthorized", "url header does not match the request.");
            return null;
        }

        Account? account = null;
        ECDsa key;
        string thumbprint;
        JsonElement? jwk = null;

        if (header.TryGetProperty("jwk", out var jwkElement))
        {
            if (!allowJwk || header.TryGetProperty("kid", out _))
            {
                await this.Problem(ctx, 400, "malformed", "jwk is only for newAccount, and never with kid.");
                return null;
            }

            jwk = jwkElement.Clone();
            key = ImportJwk(jwkElement);
            thumbprint = Thumbprint(jwkElement);
        }
        else
        {
            var kid = header.GetProperty("kid").GetString()!;
            if (!this.accounts.TryGetValue(kid, out account))
            {
                await this.Problem(ctx, 400, "accountDoesNotExist", "No such account.");
                return null;
            }

            key = account.Key;
            thumbprint = account.Thumbprint;
        }

        if (!key.VerifyData(Encoding.ASCII.GetBytes(protectedPart + "." + payloadPart), signature, HashAlgorithmName.SHA256))
        {
            await this.Problem(ctx, 401, "unauthorized", "Bad signature.");
            return null;
        }

        JsonElement? payload = payloadPart.Length == 0
            ? null
            : JsonDocument.Parse(Base64Url.DecodeFromChars(payloadPart)).RootElement;

        this.AddNonce(ctx);
        return new Verified(payload, account, key, thumbprint, jwk);
    }

    static string? VerifyEab(JsonElement binding, (string KeyId, byte[] Key) eab, JsonElement outerJwk, string url)
    {
        var protectedPart = binding.GetProperty("protected").GetString()!;
        var payloadPart = binding.GetProperty("payload").GetString()!;
        var signature = Base64Url.DecodeFromChars(binding.GetProperty("signature").GetString()!);
        var header = JsonDocument.Parse(Base64Url.DecodeFromChars(protectedPart)).RootElement;

        if (header.GetProperty("alg").GetString() != "HS256")
            return "EAB must be HS256.";
        if (header.GetProperty("kid").GetString() != eab.KeyId)
            return "Unknown EAB key id.";
        if (header.GetProperty("url").GetString() != url)
            return "EAB url mismatch.";

        var expected = HMACSHA256.HashData(eab.Key, Encoding.ASCII.GetBytes(protectedPart + "." + payloadPart));
        if (!CryptographicOperations.FixedTimeEquals(expected, signature))
            return "EAB MAC does not verify.";

        var inner = JsonDocument.Parse(Base64Url.DecodeFromChars(payloadPart)).RootElement;
        return Thumbprint(inner) == Thumbprint(outerJwk) ? null : "EAB payload is not the account key.";
    }

    static ECDsa ImportJwk(JsonElement jwk) => ECDsa.Create(new ECParameters
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = new ECPoint
        {
            X = Base64Url.DecodeFromChars(jwk.GetProperty("x").GetString()!),
            Y = Base64Url.DecodeFromChars(jwk.GetProperty("y").GetString()!)
        }
    });

    static string Thumbprint(JsonElement jwk)
    {
        var canonical = $$"""{"crv":"{{jwk.GetProperty("crv").GetString()}}","kty":"{{jwk.GetProperty("kty").GetString()}}","x":"{{jwk.GetProperty("x").GetString()}}","y":"{{jwk.GetProperty("y").GetString()}}"}""";
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    // ---- plumbing ----

    string NextId() => Interlocked.Increment(ref this.ids).ToString();

    void AddNonce(HttpContext ctx)
    {
        var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        this.nonces[nonce] = 0;
        ctx.Response.Headers["Replay-Nonce"] = nonce;
        ctx.Response.Headers["Cache-Control"] = "no-store";
    }

    ValueTask Empty(HttpContext ctx, int status)
    {
        this.AddNonce(ctx);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentLength = 0;
        return ValueTask.CompletedTask;
    }

    async ValueTask Problem(HttpContext ctx, int status, string type, string detail)
    {
        this.AddNonce(ctx);
        await Json(ctx, status, w =>
        {
            w.WriteStartObject();
            w.WriteString("type", "urn:ietf:params:acme:error:" + type);
            w.WriteString("detail", detail);
            w.WriteEndObject();
        }, "application/problem+json");
    }

    async ValueTask Json(HttpContext ctx, int status, Action<Utf8JsonWriter> write, string contentType = "application/json")
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            write(writer);

        if (!ctx.Response.Headers.ContainsKey("Replay-Nonce"))
            this.AddNonce(ctx);

        ctx.Response.StatusCode = status;
        await ctx.Response.WriteBytesAsync(buffer.ToArray(), contentType);
    }

    static (X509Certificate2 Root, X509Certificate2 Intermediate) CreateHierarchy()
    {
        var now = DateTimeOffset.UtcNow;

        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=Fake ACME Root", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        var root = rootRequest.CreateSelfSigned(now.AddDays(-1000), now.AddDays(1000));

        using var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var intermediateRequest = new CertificateRequest("CN=Fake ACME Intermediate", intermediateKey, HashAlgorithmName.SHA256);
        intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        intermediateRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(intermediateRequest.PublicKey, false));
        intermediateRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));

        using var signed = intermediateRequest.Create(root, now.AddDays(-999), now.AddDays(999), RandomNumberGenerator.GetBytes(16));
        var intermediate = signed.CopyWithPrivateKey(intermediateKey);

        return (root, intermediate);
    }

    sealed record Account(string Url, ECDsa Key, string Thumbprint);

    sealed class Order(string id, Account account)
    {
        public string Id { get; } = id;
        public Account Account { get; } = account;
        public string Status { get; set; } = "pending";
        public List<Authorization> Authorizations { get; } = [];
    }

    sealed class Authorization(string id, string type, string value, Order order)
    {
        public string Id { get; } = id;
        public string Type { get; } = type;
        public string Value { get; } = value;
        public Order Order { get; } = order;
        public string Status { get; set; } = "pending";
        public List<Challenge> Challenges { get; } = [];
    }

    sealed class Challenge(string id, string type, string token, Authorization authorization)
    {
        public string Id { get; } = id;
        public string Type { get; } = type;
        public string Token { get; } = token;
        public Authorization Authorization { get; } = authorization;
        public string Status { get; set; } = "pending";
        public string? Error { get; set; }
    }
}

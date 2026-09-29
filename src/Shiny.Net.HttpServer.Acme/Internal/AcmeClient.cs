using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer.Acme.Internal;

/// <summary>The resources a CA's directory lists (RFC 8555 §7.1.1).</summary>
sealed class AcmeDirectory
{
    public required string NewNonce { get; init; }
    public required string NewAccount { get; init; }
    public required string NewOrder { get; init; }
    public string? RenewalInfo { get; init; }
    public string? TermsOfService { get; init; }
    public bool ExternalAccountRequired { get; init; }

    public static AcmeDirectory Parse(JsonElement root)
    {
        string Required(string name) => root.TryGetProperty(name, out var value) && value.GetString() is { } url
            ? url
            : throw new AcmeException($"The ACME directory does not list '{name}'. Is the directory URL right?");

        string? termsOfService = null;
        var externalAccountRequired = false;

        if (root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            if (meta.TryGetProperty("termsOfService", out var tos))
                termsOfService = tos.GetString();

            if (meta.TryGetProperty("externalAccountRequired", out var eab) && eab.ValueKind == JsonValueKind.True)
                externalAccountRequired = true;
        }

        return new AcmeDirectory
        {
            NewNonce = Required("newNonce"),
            NewAccount = Required("newAccount"),
            NewOrder = Required("newOrder"),
            RenewalInfo = root.TryGetProperty("renewalInfo", out var ari) ? ari.GetString() : null,
            TermsOfService = termsOfService,
            ExternalAccountRequired = externalAccountRequired
        };
    }
}

/// <summary>One response from the CA, read in full.</summary>
sealed class AcmeResponse
{
    public required HttpStatusCode StatusCode { get; init; }
    public string? Location { get; init; }
    public TimeSpan? RetryAfter { get; init; }
    public required byte[] Body { get; init; }

    /// <summary>The body as JSON. ACME resources are small, so parsing on demand and cloning is fine.</summary>
    public JsonElement Json
    {
        get
        {
            using var document = JsonDocument.Parse(this.Body);
            return document.RootElement.Clone();
        }
    }

    public string Text => Encoding.UTF8.GetString(this.Body);
}

/// <summary>
/// The wire protocol: directory, nonces, signed POSTs and error documents (RFC 8555 §6–7).
/// <para>
/// Every POST is a JWS carrying a nonce the CA issued, and every response — success or error —
/// carries the next one in <c>Replay-Nonce</c>. So one nonce is kept and replaced on every response,
/// and <c>newNonce</c> is only asked when there is none. A <c>badNonce</c> error is the CA saying the
/// nonce went stale (they expire, and a CA behind a load balancer may not share them); it is retried
/// with the fresh nonce the error itself delivered, which is what §6.5 asks clients to do.
/// </para>
/// </summary>
sealed class AcmeClient
{
    const int MaxBadNonceRetries = 5;

    readonly HttpClient http;
    readonly string directoryUrl;
    readonly AcmeAccountKey key;
    readonly ILogger logger;
    string? nonce;

    public AcmeClient(HttpClient http, string directoryUrl, AcmeAccountKey key, string? accountUrl, ILogger logger)
    {
        this.http = http;
        this.directoryUrl = directoryUrl;
        this.key = key;
        this.AccountUrl = accountUrl;
        this.logger = logger;
    }

    public AcmeDirectory Directory { get; private set; } = null!;

    public string? AccountUrl { get; private set; }

    public AcmeAccountKey Key => this.key;

    public async Task LoadDirectoryAsync(CancellationToken cancellationToken)
    {
        using var response = await this.http.GetAsync(this.directoryUrl, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new AcmeException(
                $"Fetching the ACME directory {this.directoryUrl} failed with {(int)response.StatusCode} {response.StatusCode}."
            );

        using var document = JsonDocument.Parse(body);
        this.Directory = AcmeDirectory.Parse(document.RootElement);
    }

    /// <summary>
    /// Registers the account, or finds it. <c>newAccount</c> with a key the CA already knows returns the
    /// existing account (200 rather than 201), so there is no separate lookup step.
    /// </summary>
    public async Task<(string Url, bool Created)> EnsureAccountAsync(
        string? email,
        bool acceptTermsOfService,
        AcmeExternalAccountBinding? binding,
        CancellationToken cancellationToken
    )
    {
        if (this.AccountUrl is { } known)
            return (known, false);

        if (!acceptTermsOfService)
            throw new AcmeException(
                $"The CA's terms of service must be accepted to open an account: set {nameof(AcmeOptions)}." +
                $"{nameof(AcmeOptions.AcceptTermsOfService)} = true after reading them" +
                (this.Directory.TermsOfService is { } tos ? $" at {tos}." : ".")
            );

        if (this.Directory.ExternalAccountRequired && binding is null)
            throw new AcmeException(
                $"This CA requires External Account Binding. Create EAB credentials in its dashboard and set " +
                $"{nameof(AcmeOptions)}.{nameof(AcmeOptions.ExternalAccountBinding)}."
            );

        var eab = binding is null ? null : AcmeJws.ExternalAccountBinding(this.key, binding, this.Directory.NewAccount);

        var payload = AcmeJws.Json(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("termsOfServiceAgreed", true);

            if (!string.IsNullOrWhiteSpace(email))
            {
                writer.WriteStartArray("contact");
                writer.WriteStringValue(email.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? email : "mailto:" + email);
                writer.WriteEndArray();
            }

            if (eab is not null)
            {
                writer.WritePropertyName("externalAccountBinding");
                writer.WriteRawValue(eab, skipInputValidation: true);
            }

            writer.WriteEndObject();
        });

        var response = await this.PostAsync(this.Directory.NewAccount, payload, cancellationToken, useJwk: true).ConfigureAwait(false);

        this.AccountUrl = response.Location
            ?? throw new AcmeException("The CA created the account but did not say where it is (no Location header).");

        return (this.AccountUrl, response.StatusCode == HttpStatusCode.Created);
    }

    public Task<AcmeResponse> PostAsGetAsync(string url, CancellationToken cancellationToken, string? accept = null)
        => this.PostAsync(url, payload: null, cancellationToken, accept: accept);

    public async Task<AcmeResponse> PostAsync(
        string url,
        byte[]? payload,
        CancellationToken cancellationToken,
        string? accept = null,
        bool useJwk = false
    )
    {
        for (var attempt = 0; ; attempt++)
        {
            // Taken and cleared in one step: the client is shared by every certificate a registry
            // holds, and two requests signed with the same nonce means one of them is refused.
            var nonce = Interlocked.Exchange(ref this.nonce, null)
                ?? await this.NewNonceAsync(cancellationToken).ConfigureAwait(false);

            var kid = useJwk ? null : this.AccountUrl ?? throw new InvalidOperationException("No ACME account yet.");
            var body = AcmeJws.Sign(this.key, url, nonce, kid, payload);

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ByteArrayContent(body)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/jose+json");
            if (accept is not null)
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));

            using var response = await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var result = await this.ReadAsync(response, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return result;

            var error = Problem(result, url);
            if (error.ProblemType == AcmeProblems.BadNonce && attempt < MaxBadNonceRetries)
            {
                this.logger.LogDebug("The CA rejected a nonce as stale; retrying with a fresh one (attempt {Attempt})", attempt + 1);
                continue;
            }

            throw error;
        }
    }

    /// <summary>ACME Renewal Information (RFC 9773) — an unauthenticated GET, so no JWS and no nonce.</summary>
    public async Task<AcmeResponse?> GetRenewalInfoAsync(string certificateId, CancellationToken cancellationToken)
    {
        if (this.Directory.RenewalInfo is not { } baseUrl)
            return null;

        using var response = await this.http
            .GetAsync(baseUrl.TrimEnd('/') + "/" + certificateId, cancellationToken)
            .ConfigureAwait(false);

        var result = await this.ReadAsync(response, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? result : null;
    }

    async Task<string> NewNonceAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, this.Directory.NewNonce);
        using var response = await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        return ReplayNonce(response)
            ?? throw new AcmeException($"The CA's newNonce resource returned {(int)response.StatusCode} without a Replay-Nonce header.");
    }

    async Task<AcmeResponse> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Every response carries the next nonce, errors included. Keeping it saves a round trip per request.
        if (ReplayNonce(response) is { } next)
            this.nonce = next;

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        return new AcmeResponse
        {
            StatusCode = response.StatusCode,
            Location = response.Headers.Location is { } location
                ? (location.IsAbsoluteUri ? location.AbsoluteUri : new Uri(response.RequestMessage!.RequestUri!, location).AbsoluteUri)
                : null,
            RetryAfter = RetryAfter(response),
            Body = body
        };
    }

    static string? ReplayNonce(HttpResponseMessage response)
        => response.Headers.TryGetValues("Replay-Nonce", out var values) ? values.FirstOrDefault() : null;

    internal static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } retryAfter)
            return null;

        if (retryAfter.Delta is { } delta)
            return delta;

        if (retryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    static AcmeException Problem(AcmeResponse response, string url)
    {
        string? type = null;
        string? detail = null;
        var subproblems = new List<string>();

        try
        {
            var json = response.Json;
            if (json.ValueKind == JsonValueKind.Object)
            {
                type = json.TryGetProperty("type", out var t) ? t.GetString() : null;
                detail = json.TryGetProperty("detail", out var d) ? d.GetString() : null;

                if (json.TryGetProperty("subproblems", out var subs) && subs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var sub in subs.EnumerateArray())
                        subproblems.Add(DescribeProblem(sub));
                }
            }
        }
        catch (JsonException)
        {
            detail = response.Text;
        }

        var message = new StringBuilder($"The CA rejected {url}: {(int)response.StatusCode}");
        if (type is not null)
            message.Append(' ').Append(type);
        if (detail is not null)
            message.Append(" — ").Append(detail);
        foreach (var sub in subproblems)
            message.Append("; ").Append(sub);

        return new AcmeException(message.ToString(), type, detail, response.StatusCode, response.RetryAfter, subproblems);
    }

    /// <summary>One problem object (an authorization's or challenge's <c>error</c>, or a subproblem) as a line of text.</summary>
    internal static string DescribeProblem(JsonElement problem)
    {
        var type = problem.TryGetProperty("type", out var t) ? t.GetString() : null;
        var detail = problem.TryGetProperty("detail", out var d) ? d.GetString() : null;
        var identifier = problem.TryGetProperty("identifier", out var i) && i.TryGetProperty("value", out var v)
            ? v.GetString() + ": "
            : "";

        return $"{identifier}{type} {detail}".Trim();
    }
}

/// <summary>Problem type URNs this client acts on (RFC 8555 §6.7).</summary>
static class AcmeProblems
{
    public const string BadNonce = "urn:ietf:params:acme:error:badNonce";
    public const string RateLimited = "urn:ietf:params:acme:error:rateLimited";
}

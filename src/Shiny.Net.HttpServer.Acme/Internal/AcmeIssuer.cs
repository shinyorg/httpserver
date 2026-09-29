using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer.Acme.Internal;

/// <summary>
/// One order, start to certificate (RFC 8555 §7.4): newOrder → authorizations → challenges →
/// finalize with a CSR → poll → download the chain.
/// </summary>
sealed class AcmeIssuer(
    AcmeClient client,
    AcmeOptions options,
    AcmeChallengeResponder challenges,
    ILogger logger
)
{
    const string Http01 = "http-01";
    const string TlsAlpn01 = "tls-alpn-01";

    /// <summary>Runs the order and returns the certificate, key and chain as PKCS#12.</summary>
    public async Task<byte[]> IssueAsync(
        IReadOnlyList<AcmeIdentifier> identifiers,
        string? replaces,
        CancellationToken cancellationToken
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.OrderTimeout);

        try
        {
            return await this.RunAsync(identifiers, replaces, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new AcmeException(
                $"The order did not complete within {options.OrderTimeout}. It will be retried; the CA's order may still be pending."
            );
        }
    }

    byte[] NewOrderPayload(IReadOnlyList<AcmeIdentifier> identifiers, string? replaces) => AcmeJws.Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartArray("identifiers");
        foreach (var identifier in identifiers)
        {
            writer.WriteStartObject();
            writer.WriteString("type", identifier.Type);
            writer.WriteString("value", identifier.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        if (options.Profile is { } profile)
            writer.WriteString("profile", profile);

        // RFC 9773 §5: tells the CA which certificate this one replaces, which is what exempts a
        // renewal it asked for from rate limits.
        if (replaces is not null)
            writer.WriteString("replaces", replaces);

        writer.WriteEndObject();
    });

    async Task<byte[]> RunAsync(IReadOnlyList<AcmeIdentifier> identifiers, string? replaces, CancellationToken cancellationToken)
    {
        AcmeResponse created;
        try
        {
            created = await client.PostAsync(client.Directory.NewOrder, NewOrderPayload(identifiers, replaces), cancellationToken).ConfigureAwait(false);
        }
        catch (AcmeException ex) when (replaces is not null
            && ex.ProblemType is not ("urn:ietf:params:acme:error:rateLimited" or "urn:ietf:params:acme:error:accountDoesNotExist"))
        {
            // A CA may refuse to let this account replace a certificate it did not issue to it — the
            // usual case for one imported from another ACME client. Naming it was only a courtesy
            // (it exempts an ARI-requested renewal from rate limits), so order again without it.
            logger.LogInformation(
                "The CA refused the order naming the certificate it replaces ({Problem}); ordering without it",
                ex.ProblemType ?? ex.Message
            );

            created = await client.PostAsync(client.Directory.NewOrder, NewOrderPayload(identifiers, null), cancellationToken).ConfigureAwait(false);
        }

        var orderUrl = created.Location
            ?? throw new AcmeException("The CA created the order but did not say where it is (no Location header).");

        var order = created.Json;
        logger.LogDebug("ACME order {Order} created, status {Status}", orderUrl, Status(order));

        if (order.TryGetProperty("authorizations", out var authorizations))
        {
            foreach (var authorization in authorizations.EnumerateArray())
                await this.AuthorizeAsync(authorization.GetString()!, cancellationToken).ConfigureAwait(false);
        }

        order = await this.PollAsync(orderUrl, s => s is not "pending", cancellationToken).ConfigureAwait(false);

        using var key = AcmeCertificates.CreateKey(options);

        if (Status(order) == "ready")
        {
            var finalize = order.GetProperty("finalize").GetString()!;
            var csr = AcmeCertificates.CreateCsr(key, identifiers);

            var finalized = await client
                .PostAsync(finalize, AcmeJws.Json(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("csr", csr);
                    w.WriteEndObject();
                }), cancellationToken)
                .ConfigureAwait(false);

            order = finalized.Json;
            if (Status(order) is "processing" or "ready")
                order = await this.PollAsync(orderUrl, s => s is not ("processing" or "ready"), cancellationToken).ConfigureAwait(false);
        }

        if (Status(order) != "valid")
            throw new AcmeException($"The order ended {Status(order)} instead of valid.{Error(order)}");

        var certificateUrl = order.GetProperty("certificate").GetString()!;
        var download = await client
            .PostAsGetAsync(certificateUrl, cancellationToken, accept: "application/pem-certificate-chain")
            .ConfigureAwait(false);

        return AcmeCertificates.BuildPkcs12(download.Text, key);
    }

    async Task AuthorizeAsync(string url, CancellationToken cancellationToken)
    {
        var authorization = (await client.PostAsGetAsync(url, cancellationToken).ConfigureAwait(false)).Json;
        var identifier = authorization.GetProperty("identifier");
        var type = identifier.GetProperty("type").GetString();
        var name = identifier.GetProperty("value").GetString()!;

        switch (Status(authorization))
        {
            case "valid":
                // The CA remembers a recent validation (Let's Encrypt: 30 days) and does not ask again.
                logger.LogDebug("{Name} is already authorized", name);
                return;
            case "pending":
                break;
            default:
                throw new AcmeException($"The authorization for {name} is {Status(authorization)}.{Error(authorization)}");
        }

        var (challenge, kind) = this.Choose(authorization, type, name);
        var token = challenge.GetProperty("token").GetString()!;
        var challengeUrl = challenge.GetProperty("url").GetString()!;
        var keyAuthorization = client.Key.KeyAuthorization(token);

        if (kind == Http01)
            challenges.AddHttp(token, keyAuthorization);
        else
            challenges.AddTlsAlpn(name, keyAuthorization);

        try
        {
            logger.LogInformation(
                "Proving control of {Name} with {Challenge}{Where}",
                name,
                kind,
                kind == Http01 ? $" — the CA will fetch http://{name}/.well-known/acme-challenge/{token} on port 80" : " — the CA will connect to port 443"
            );

            if (Status(challenge) == "pending")
                await client.PostAsync(challengeUrl, "{}"u8.ToArray(), cancellationToken).ConfigureAwait(false);

            authorization = await this.PollAsync(url, s => s is not "pending", cancellationToken).ConfigureAwait(false);

            if (Status(authorization) != "valid")
            {
                var detail = Error(authorization);
                if (detail.Length == 0 && authorization.TryGetProperty("challenges", out var all))
                {
                    foreach (var c in all.EnumerateArray())
                    {
                        if (c.TryGetProperty("error", out var error))
                            detail = " " + AcmeClient.DescribeProblem(error);
                    }
                }

                throw new AcmeException($"Validating {name} with {kind} failed ({Status(authorization)}).{detail}");
            }

            logger.LogInformation("{Name} validated", name);
        }
        finally
        {
            if (kind == Http01)
                challenges.RemoveHttp(token);
            else
                challenges.RemoveTlsAlpn(name);
        }
    }

    (JsonElement Challenge, string Kind) Choose(JsonElement authorization, string? identifierType, string name)
    {
        JsonElement? http = null, tls = null;
        var offered = new List<string>();

        foreach (var challenge in authorization.GetProperty("challenges").EnumerateArray())
        {
            var kind = challenge.GetProperty("type").GetString();
            offered.Add(kind ?? "?");

            if (kind == Http01)
                http = challenge;
            else if (kind == TlsAlpn01)
                tls = challenge;
        }

        if (options.Challenges.HasFlag(AcmeChallengeTypes.Http01) && http is { } h)
            return (h, Http01);

        // RFC 8737 names the host by SNI, which cannot carry an IP address, so TLS-ALPN-01 is DNS only here.
        if (options.Challenges.HasFlag(AcmeChallengeTypes.TlsAlpn01) && tls is { } t && identifierType == AcmeIdentifier.Dns)
            return (t, TlsAlpn01);

        throw new AcmeException(
            $"The CA offered {string.Join(", ", offered)} for {name}, and none of them is enabled in " +
            $"{nameof(AcmeOptions)}.{nameof(AcmeOptions.Challenges)} ({options.Challenges})."
        );
    }

    /// <summary>
    /// Polls a resource with POST-as-GET until <paramref name="done"/> says so. <c>Retry-After</c> from the
    /// CA is honoured (it is the CA telling us how busy it is); otherwise <see cref="AcmeOptions.PollInterval"/>.
    /// The order timeout bounds the whole thing.
    /// </summary>
    async Task<JsonElement> PollAsync(string url, Func<string?, bool> done, CancellationToken cancellationToken)
    {
        while (true)
        {
            var response = await client.PostAsGetAsync(url, cancellationToken).ConfigureAwait(false);
            var json = response.Json;

            if (done(Status(json)))
                return json;

            var delay = response.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero
                ? retryAfter
                : options.PollInterval;

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    static string? Status(JsonElement resource)
        => resource.TryGetProperty("status", out var status) ? status.GetString() : null;

    static string Error(JsonElement resource)
        => resource.TryGetProperty("error", out var error) ? " " + AcmeClient.DescribeProblem(error) : "";
}

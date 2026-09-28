using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer.Acme.Internal;
using Shiny.Net.HttpServer.Http3;

namespace Shiny.Net.HttpServer.Acme;

/// <summary>Registering ACME with the server builder.</summary>
public static class AcmeServiceExtensions
{
    /// <summary>
    /// Obtains and renews a certificate from an ACME CA and serves it on every HTTPS endpoint that has no
    /// certificate of its own.
    /// <code>
    /// builder.Options.Listen(IPAddress.Any, 80);                                 // HTTP-01 is always port 80
    /// builder.Options.Listen(IPAddress.Any, 443).Https = new HttpsOptions();     // no certificate: ACME's
    ///
    /// builder.AddAcme(o =>
    /// {
    ///     o.Domains = ["example.com"];
    ///     o.Email = "ops@example.com";
    ///     o.AcceptTermsOfService = true;
    ///     o.DirectoryUrl = AcmeDirectories.LetsEncryptStaging;   // until it works; then drop this line
    /// });
    /// </code>
    /// <para>
    /// Once the server is running, a background loop loads the stored certificate or orders one, answers
    /// the CA's challenge from this same server, and renews at two thirds of the lifetime (or when the CA's
    /// renewal information says), swapping each new certificate in without a restart.
    /// </para>
    /// <para>
    /// A second call adds to the same options rather than replacing them.
    /// </para>
    /// </summary>
    public static ShinyHttpServerBuilder AddAcme(this ShinyHttpServerBuilder builder, Action<AcmeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        OptionsRegistration.Configure(builder.Services, configure);

        if (!builder.Services.Any(x => x.ServiceType == typeof(AcmeCertificateManager)))
        {
            builder.Services.AddSingleton(sp => new AcmeCertificateManager(
                sp.GetRequiredService<AcmeOptions>(),
                sp.GetService<ILoggerFactory>()
            ));

            builder.Configure(server => server.UseAcme(server.Services!.GetRequiredService<AcmeCertificateManager>()));
        }

        return builder;
    }
}

/// <summary>Wiring an <see cref="AcmeCertificateManager"/> into a server and its listeners.</summary>
public static class HttpServerAcmeExtensions
{
    /// <summary>
    /// Connects <paramref name="manager"/> to <paramref name="server"/>: answers HTTP-01 challenges at
    /// <c>/.well-known/acme-challenge/</c>, serves the certificate on every HTTPS endpoint configured
    /// without one, and runs the renewal loop while the server is running.
    /// <para>
    /// The challenge middleware goes in where this is called, so call it before anything that would turn
    /// the CA's plain GET away — authentication, an IP filter, an HTTPS redirect is fine (the CA follows it).
    /// <c>AddAcme</c> calls this for you.
    /// </para>
    /// </summary>
    public static HttpServer UseAcme(this HttpServer server, AcmeCertificateManager manager)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(manager);

        server.Use(async (context, next) =>
        {
            var request = context.Request;
            if (request.Path.StartsWith(AcmeChallengeResponder.HttpChallengePrefix, StringComparison.Ordinal)
                && (request.Method == HttpMethods.Get || request.Method == HttpMethods.Head)
                && manager.Challenges.TryGetHttp(request.Path[AcmeChallengeResponder.HttpChallengePrefix.Length..], out var keyAuthorization))
            {
                // RFC 8555 §8.3: the body is the key authorization, nothing else.
                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.WriteTextAsync(keyAuthorization, "application/octet-stream").ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });

        var logger = server.Services?.GetService<ILoggerFactory>()?.CreateLogger<AcmeCertificateManager>();

        // Attached now so the endpoints are wired even before a start; reported on at start, once the
        // endpoint list is final.
        AttachEndpoints(server.Options, manager, logger: null);

        server.StateChanged += (_, state) =>
        {
            switch (state)
            {
                case HttpServerState.Starting:
                    // Again at every start, so an endpoint added after UseAcme — or a restart with new
                    // endpoints — is still covered.
                    AttachEndpoints(server.Options, manager, logger);
                    break;
                case HttpServerState.Running:
                    manager.StartRenewal();
                    break;
                case HttpServerState.Stopping:
                    _ = manager.StopRenewalAsync();
                    break;
            }
        };

        if (server.IsRunning)
            manager.StartRenewal();

        return server;
    }

    /// <summary>
    /// Serves the ACME certificate on this endpoint, and answers TLS-ALPN-01 on it when that challenge
    /// is enabled. What <see cref="UseAcme(HttpServer, AcmeCertificateManager)"/> does to every HTTPS
    /// endpoint without a certificate; call it directly on one that has a fallback certificate set.
    /// </summary>
    public static HttpsOptions UseAcme(this HttpsOptions https, AcmeCertificateManager manager)
    {
        ArgumentNullException.ThrowIfNull(https);
        ArgumentNullException.ThrowIfNull(manager);

        https.CertificateContextSelector = manager.SelectCertificate;

        if (manager.Options.Challenges.HasFlag(AcmeChallengeTypes.TlsAlpn01))
            https.ChallengeResponder = manager.Challenges.Respond;

        return https;
    }

    /// <summary>
    /// Serves the ACME certificate on the HTTP/3 listener, renewals included — it is read per connection.
    /// <code>
    /// await app.ListenHttp3Async(o =>
    /// {
    ///     o.Address = IPAddress.Any;
    ///     o.Port = 443;
    ///     o.UseAcme(app);
    /// });
    /// </code>
    /// TLS-ALPN-01 is never answered over QUIC: RFC 8737 validates over TCP only.
    /// </summary>
    public static Http3Options UseAcme(this Http3Options http3, AcmeCertificateManager manager)
    {
        ArgumentNullException.ThrowIfNull(http3);
        ArgumentNullException.ThrowIfNull(manager);

        http3.CertificateContextSelector = manager.SelectCertificate;
        return http3;
    }

    /// <summary>Serves the certificate of the manager <c>AddAcme</c> registered on the HTTP/3 listener.</summary>
    public static Http3Options UseAcme(this Http3Options http3, HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return http3.UseAcme(server.GetAcmeCertificateManager());
    }

    /// <summary>The manager <c>AddAcme</c> registered — for its events, or to renew by hand.</summary>
    public static AcmeCertificateManager GetAcmeCertificateManager(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        return server.Services?.GetService<AcmeCertificateManager>()
            ?? throw new InvalidOperationException("No AcmeCertificateManager is registered. Call builder.AddAcme(...) first.");
    }

    static void AttachEndpoints(HttpServerOptions options, AcmeCertificateManager manager, ILogger? logger)
    {
        var serving = 0;
        var port80 = false;

        foreach (var endpoint in options.Endpoints.Count > 0 ? options.Endpoints : [new HttpServerEndpoint(options.Address, options.Port) { Https = options.Https }])
        {
            if (endpoint.Https is not { } https)
            {
                port80 |= endpoint.Port == 80;
                continue;
            }

            Func<string?, System.Net.Security.SslStreamCertificateContext?> selector = manager.SelectCertificate;

            if (https.CertificateContextSelector is null && https.CertificateSelector is null && https.Certificate is null)
                https.UseAcme(manager);

            if (Equals(https.CertificateContextSelector, selector))
                serving++;
        }

        if (serving == 0)
            logger?.LogWarning(
                "ACME is configured but no HTTPS endpoint serves its certificate. Add one without a certificate " +
                "(endpoint.Https = new HttpsOptions()) or call https.UseAcme(manager) on it"
            );

        if (!port80 && manager.Options.Challenges.HasFlag(AcmeChallengeTypes.Http01))
            logger?.LogInformation(
                "HTTP-01 validation connects to port 80 and no cleartext endpoint is bound there — fine if a router or " +
                "proxy forwards port 80 to this server, otherwise validation will fail"
            );
    }
}

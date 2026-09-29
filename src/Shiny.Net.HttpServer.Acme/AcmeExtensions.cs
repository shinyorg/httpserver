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

    /// <summary>
    /// Registers an <see cref="AcmeCertificateRegistry"/> — many certificates, chosen per connection by
    /// SNI, with entries added at runtime — and wires it to the server: one HTTP-01 middleware for every
    /// entry, every HTTPS endpoint without a certificate of its own, and renewal while the server runs.
    /// <code>
    /// builder.Options.Listen(IPAddress.Any, 80);
    /// builder.Options.Listen(IPAddress.Any, 443).Https = new HttpsOptions();
    ///
    /// builder.AddAcmeRegistry(o =>
    /// {
    ///     o.Email = "ops@example.com";
    ///     o.AcceptTermsOfService = true;
    /// });
    ///
    /// var server = builder.Build();
    /// var acme = server.GetAcmeCertificateRegistry();
    /// acme.Set("site", ["example.com", "www.example.com"]);
    /// await acme.IssueAsync("site");                 // nothing is ever ordered implicitly
    /// </code>
    /// A second call adds to the same options rather than replacing them.
    /// </summary>
    public static ShinyHttpServerBuilder AddAcmeRegistry(this ShinyHttpServerBuilder builder, Action<AcmeRegistryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        OptionsRegistration.Configure(builder.Services, configure);

        if (!builder.Services.Any(x => x.ServiceType == typeof(AcmeCertificateRegistry)))
        {
            builder.Services.AddSingleton(sp => new AcmeCertificateRegistry(
                sp.GetRequiredService<AcmeRegistryOptions>(),
                sp.GetService<ILoggerFactory>()
            ));

            builder.Configure(server => server.UseAcme(server.Services!.GetRequiredService<AcmeCertificateRegistry>()));
        }

        return builder;
    }
}

/// <summary>Wiring an <see cref="AcmeCertificateManager"/> or <see cref="AcmeCertificateRegistry"/> into a server and its listeners.</summary>
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

        UseChallengeMiddleware(server, manager.Challenges);

        var logger = server.Services?.GetService<ILoggerFactory>()?.CreateLogger<AcmeCertificateManager>();
        Func<string?, System.Net.Security.SslStreamCertificateContext?> selector = manager.SelectCertificate;

        WireLifetime(
            server,
            attach: log => AttachEndpoints(server.Options, selector, https => https.UseAcme(manager), manager.Options.Challenges, log),
            start: manager.StartRenewal,
            stop: manager.StopRenewalAsync,
            logger
        );

        return server;
    }

    /// <summary>
    /// Connects <paramref name="registry"/> to <paramref name="server"/>: one middleware answers HTTP-01 at
    /// <c>/.well-known/acme-challenge/</c> for every entry — including entries added after the server
    /// started — every HTTPS endpoint configured without a certificate selects the entry's certificate
    /// by SNI, and entries with a certificate renew while the server is running.
    /// <para>
    /// The challenge middleware goes in where this is called, so call it before anything that would turn
    /// the CA's plain GET away. <c>AddAcmeRegistry</c> calls this for you.
    /// </para>
    /// </summary>
    public static HttpServer UseAcme(this HttpServer server, AcmeCertificateRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(registry);

        UseChallengeMiddleware(server, registry.Challenges);

        var logger = server.Services?.GetService<ILoggerFactory>()?.CreateLogger<AcmeCertificateRegistry>();
        Func<string?, System.Net.Security.SslStreamCertificateContext?> selector = registry.SelectCertificate;

        WireLifetime(
            server,
            attach: log => AttachEndpoints(server.Options, selector, https => https.UseAcme(registry), registry.Options.Challenges, log),
            start: registry.StartRenewal,
            stop: registry.StopRenewalAsync,
            logger
        );

        return server;
    }

    /// <summary>
    /// Serves the registry's certificates on this endpoint, chosen by SNI, and answers TLS-ALPN-01 on it
    /// for every entry when that challenge is enabled.
    /// </summary>
    public static HttpsOptions UseAcme(this HttpsOptions https, AcmeCertificateRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(https);
        ArgumentNullException.ThrowIfNull(registry);

        https.CertificateContextSelector = registry.SelectCertificate;

        if (registry.Options.Challenges.HasFlag(AcmeChallengeTypes.TlsAlpn01))
            https.ChallengeResponder = registry.Challenges.Respond;

        return https;
    }

    /// <summary>
    /// Serves the registry's certificates on the HTTP/3 listener, chosen by SNI per connection, renewals
    /// and entries added later included. TLS-ALPN-01 is never answered over QUIC (RFC 8737 is TCP only).
    /// </summary>
    public static Http3Options UseAcme(this Http3Options http3, AcmeCertificateRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(http3);
        ArgumentNullException.ThrowIfNull(registry);

        http3.CertificateContextSelector = registry.SelectCertificate;
        return http3;
    }

    /// <summary>The registry <c>AddAcmeRegistry</c> registered.</summary>
    public static AcmeCertificateRegistry GetAcmeCertificateRegistry(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        return server.Services?.GetService<AcmeCertificateRegistry>()
            ?? throw new InvalidOperationException("No AcmeCertificateRegistry is registered. Call builder.AddAcmeRegistry(...) first.");
    }

    static void UseChallengeMiddleware(HttpServer server, AcmeChallengeResponder challenges)
        => server.Use(async (context, next) =>
        {
            var request = context.Request;
            if (request.Path.StartsWith(AcmeChallengeResponder.HttpChallengePrefix, StringComparison.Ordinal)
                && (request.Method == HttpMethods.Get || request.Method == HttpMethods.Head)
                && challenges.TryGetHttp(request.Path[AcmeChallengeResponder.HttpChallengePrefix.Length..], out var keyAuthorization))
            {
                // RFC 8555 §8.3: the body is the key authorization, nothing else.
                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.WriteTextAsync(keyAuthorization, "application/octet-stream").ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });

    static void WireLifetime(HttpServer server, Action<ILogger?> attach, Action start, Func<Task> stop, ILogger? logger)
    {
        // Attached now so the endpoints are wired even before a start; reported on at start, once the
        // endpoint list is final.
        attach(null);

        server.StateChanged += (_, state) =>
        {
            switch (state)
            {
                case HttpServerState.Starting:
                    // Again at every start, so an endpoint added after UseAcme — or a restart with new
                    // endpoints — is still covered.
                    attach(logger);
                    break;
                case HttpServerState.Running:
                    start();
                    break;
                case HttpServerState.Stopping:
                    _ = stop();
                    break;
            }
        };

        if (server.IsRunning)
            start();
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

    /// <summary>
    /// Serves the certificate of the manager <c>AddAcme</c> registered on the HTTP/3 listener — or, when
    /// only <c>AddAcmeRegistry</c> was called, the registry's certificates by SNI.
    /// </summary>
    public static Http3Options UseAcme(this Http3Options http3, HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (server.Services?.GetService<AcmeCertificateManager>() is null && server.Services?.GetService<AcmeCertificateRegistry>() is { } registry)
            return http3.UseAcme(registry);

        return http3.UseAcme(server.GetAcmeCertificateManager());
    }

    /// <summary>The manager <c>AddAcme</c> registered — for its events, or to renew by hand.</summary>
    public static AcmeCertificateManager GetAcmeCertificateManager(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        return server.Services?.GetService<AcmeCertificateManager>()
            ?? throw new InvalidOperationException("No AcmeCertificateManager is registered. Call builder.AddAcme(...) first.");
    }

    static void AttachEndpoints(
        HttpServerOptions options,
        Func<string?, System.Net.Security.SslStreamCertificateContext?> selector,
        Action<HttpsOptions> useAcme,
        AcmeChallengeTypes challenges,
        ILogger? logger
    )
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

            if (https.CertificateContextSelector is null && https.CertificateSelector is null && https.Certificate is null)
                useAcme(https);

            if (Equals(https.CertificateContextSelector, selector))
                serving++;
        }

        if (serving == 0)
            logger?.LogWarning(
                "ACME is configured but no HTTPS endpoint serves its certificate. Add one without a certificate " +
                "(endpoint.Https = new HttpsOptions()) or call https.UseAcme(...) on it"
            );

        if (!port80 && challenges.HasFlag(AcmeChallengeTypes.Http01))
            logger?.LogInformation(
                "HTTP-01 validation connects to port 80 and no cleartext endpoint is bound there — fine if a router or " +
                "proxy forwards port 80 to this server, otherwise validation will fail"
            );
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Caching;
using Shiny.Net.HttpServer.Compression;
using Shiny.Net.HttpServer.Cors;
using Shiny.Net.HttpServer.Discovery;
using Shiny.Net.HttpServer.Jwt;
using Shiny.Net.HttpServer.Logging;
using Shiny.Net.HttpServer.Mcp;
using Shiny.Net.HttpServer.Mobile;
using Shiny.Net.HttpServer.RateLimiting;
using Shiny.Net.HttpServer.Security;
using Shiny.Net.HttpServer.Sessions;
using Shiny.Net.HttpServer.Telemetry;
using Shiny.Net.HttpServer.Timeouts;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// Calling an <c>Add…</c> registration more than once. Each used to register its options with
/// <c>TryAddSingleton</c> around the one <c>configure</c> it was given, so every later call was dropped
/// without a word — and a policy named only in a later call surfaced as a 500 on the first request
/// that asked for it. The builder already promises that a second registration adopts the first rather
/// than configuring something nothing reads; these hold every registration to that.
/// </summary>
public class RegistrationCompositionTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static readonly byte[] Secret = Encoding.UTF8.GetBytes("tests-32-byte-secret-value!!!!!!");
    const string Audience = "composition-tests";

    static readonly Dictionary<string, (Action<ShinyHttpServerBuilder, Action<object>> Register, Type Options)> Cases = new()
    {
        ["AddAuthorization"] = ((b, c) => b.AddAuthorization(o => c(o)), typeof(AuthorizationOptions)),
        ["AddIpFilter"] = ((b, c) => b.AddIpFilter(o => c(o)), typeof(IpFilterOptions)),
        ["AddAntiforgery"] = ((b, c) => b.AddAntiforgery(o => c(o)), typeof(AntiforgeryOptions)),
        ["AddRequestTimeouts"] = ((b, c) => b.AddRequestTimeouts(o => c(o)), typeof(RequestTimeoutOptions)),
        ["AddRateLimiter"] = ((b, c) => b.AddRateLimiter(o => c(o)), typeof(RateLimitOptions)),
        ["AddSessions"] = ((b, c) => b.AddSessions(o => c(o)), typeof(SessionOptions)),
        ["AddRequestDecompression"] = ((b, c) => b.AddRequestDecompression(o => c(o)), typeof(RequestDecompressionOptions)),
        ["AddProblemDetails"] = ((b, c) => b.AddProblemDetails(o => c(o)), typeof(ProblemDetailsOptions)),
        ["AddCors"] = ((b, c) => b.AddCors(o => c(o)), typeof(CorsOptions)),
        ["AddHttpServerTelemetry"] = ((b, c) => b.AddHttpServerTelemetry(o => c(o)), typeof(TelemetryOptions)),
        ["AddOutputCache"] = ((b, c) => b.AddOutputCache(o => c(o)), typeof(OutputCacheOptions)),
        ["AddW3CLogging"] = ((b, c) => b.AddW3CLogging(o => c(o)), typeof(W3CLoggerOptions)),
        // JWT refuses half-configured options when they are built, so each call supplies a valid minimum.
        ["AddJwtBearer"] = ((b, c) => b.AddAuthentication().AddJwtBearer(o =>
        {
            o.SigningKey ??= JwtSigningKey.FromSecret(Secret);
            o.Issuer ??= "tests";
            o.Audience ??= "tests";
            c(o);
        }), typeof(JwtBearerOptions)),
        ["AddHttpServerLifecycle"] = ((b, c) => b.AddHttpServerLifecycle(o => c(o)), typeof(HttpServerLifecycleOptions)),
        ["AddHttpServerAdvertisement"] = ((b, c) => b.AddHttpServerAdvertisement(o => c(o)), typeof(HttpServerAdvertisementOptions)),
        ["AddMcpProtectedResource"] = ((b, c) => b.AddMcpProtectedResource(o => c(o)), typeof(McpProtectedResourceOptions)),
    };

    public static TheoryData<string> Registrations => [.. Cases.Keys];

    [Theory]
    [MemberData(nameof(Registrations))]
    public async Task Every_call_configures_the_options_that_are_resolved(string registration)
    {
        var (register, type) = Cases[registration];
        var builder = HttpServer.CreateBuilder();
        var configured = new List<object>();

        register(builder, configured.Add);
        register(builder, configured.Add);

        await using var provider = builder.Services.BuildServiceProvider();
        var resolved = provider.GetRequiredService(type);

        // Both calls ran, in order, against the one instance everything downstream reads.
        Assert.Equal(2, configured.Count);
        Assert.All(configured, options => Assert.Same(resolved, options));

        // And registering twice still registers the options once.
        Assert.Single(builder.Services, x => x.ServiceType == type);
    }

    /// <summary>The report that started this: a policy from a second call, asked for by an endpoint.</summary>
    [Fact]
    public async Task A_policy_from_a_later_AddAuthorization_is_enforced()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseAuthentication();
                app.UseAuthorization();
                app.MapGet("/read", ctx => ctx.Response.WriteAsync("read")).RequireAuthorization("readers");
                app.MapGet("/write", ctx => ctx.Response.WriteAsync("write")).RequireAuthorization("writers");
            },
            builder =>
            {
                builder.AddAuthentication().AddApiKey(o => o
                    .AddKey("reader", "reader", "reader")
                    .AddKey("both", "both", "reader", "writer")
                );

                builder.AddAuthorization(o => o.AddPolicy("readers", p => p.RequireRole("reader")));
                builder.AddAuthorization(o => o.AddPolicy("writers", p => p.RequireRole("writer")));
            }
        );

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(server, "/write", "both"));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(server, "/write", "reader"));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(server, "/read", "reader"));
    }

    /// <summary>JWT builds its validation from the options once they are complete — after every call, not after the first.</summary>
    [Fact]
    public async Task Jwt_validation_is_built_from_every_call()
    {
        const string issuer = "split-issuer";

        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseAuthentication();
                app.UseAuthorization();
                app.MapGet("/me", ctx => ctx.Response.WriteAsync("me")).RequireAuthorization();
            },
            builder =>
            {
                // Neither call alone is a valid configuration — JWT would refuse it — so this only works if
                // validation is built after both.
                var authentication = builder.AddAuthentication();
                authentication.AddJwtBearer(o =>
                {
                    o.SigningKey = JwtSigningKey.FromSecret(Secret);
                    o.Audience = Audience;
                });
                authentication.AddJwtBearer(o => o.Issuer = issuer);
                builder.AddAuthorization();
            }
        );

        using var key = JwtSigningKey.FromSecret(Secret);
        var generator = new JwtTokenGenerator(key);

        var matching = generator.Create(new JwtTokenDescriptor { Issuer = issuer, Subject = "u", Audiences = { Audience } });
        var foreign = generator.Create(new JwtTokenDescriptor { Issuer = "someone-else", Subject = "u", Audiences = { Audience } });

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(server, "/me", bearer: matching));

        // Only true if the issuer from the second call made it into validation.
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(server, "/me", bearer: foreign));
    }

    static async Task<HttpStatusCode> StatusAsync(TestServer server, string path, string? apiKey = null, string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (apiKey is not null)
            request.Headers.Add("X-API-Key", apiKey);

        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        using var response = await server.Client.SendAsync(request, Token);
        return response.StatusCode;
    }
}

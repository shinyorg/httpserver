using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Shiny.Net.HttpServer.OpenApi;
using Shiny.Net.HttpServer.Routing;
using Shiny.Net.HttpServer.SourceGenerators;
using Shiny.Net.HttpServer.Testing;
using Shiny.Net.HttpServer.Versioning;

namespace Shiny.Net.HttpServer.Tests;

// ---------------------------------------------------------------------------
// Generated, versioned endpoints. Declared at compile time with [ApiVersion] /
// [MapToApiVersion]; the generator emits ApiVersionMetadata on each route.
// ---------------------------------------------------------------------------

/// <summary>Users, three versions: 0.9 (deprecated), 1.0 and 2.0 — the last with its own handler.</summary>
[Route("/versioned-gen/users")]
[ApiVersion("0.9", Deprecated = true)]
[ApiVersion("1.0")]
[ApiVersion(2.0)]
public class VersionedUserEndpoints
{
    /// <summary>Lists users the old way.</summary>
    [Get]
    public string List(HttpContext ctx) => "users@" + ctx.GetRequestedApiVersion();

    /// <summary>Lists users the new way.</summary>
    [Get]
    [MapToApiVersion("2.0")]
    public string ListV2() => "users-v2";
}

/// <summary>URL-segment versioning on a generated class.</summary>
[Route("/versioned-gen/{version:apiVersion}/orders")]
[ApiVersion("1.0")]
[ApiVersion("2.0")]
public class VersionedOrderEndpoints
{
    [Get("/{id:int}")]
    [MapToApiVersion("1.0")]
    public string GetV1(int id) => "order-v1-" + id;

    [Get("/{id:int}")]
    [MapToApiVersion("2.0")]
    public string GetV2(int id) => "order-v2-" + id;
}

/// <summary>Version-neutral: any version, or none.</summary>
[Route("/versioned-gen/health")]
[ApiVersionNeutral]
public class VersionNeutralEndpoints
{
    [Get]
    public string Health() => "healthy";
}

/// <summary>How a test reaches the server: over a socket or through memory, at HTTP/1.1 or HTTP/2.</summary>
public enum VersioningTransport
{
    Http1,
    Http2,
    InMemoryHttp1,
    InMemoryHttp2
}

public class ApiVersionTypeTests
{
    [Theory]
    [InlineData("1", 1, null, null)]
    [InlineData("1.0", 1, 0, null)]
    [InlineData("v2", 2, null, null)]
    [InlineData("V2.1", 2, 1, null)]
    [InlineData("1.0-beta", 1, 0, "beta")]
    [InlineData("3-rc1", 3, null, "rc1")]
    public void Parses_numeric_versions(string text, int major, int? minor, string? status)
    {
        var version = ApiVersion.Parse(text);

        Assert.Null(version.GroupVersion);
        Assert.Equal(major, version.MajorVersion);
        Assert.Equal(minor, version.MinorVersion);
        Assert.Equal(status, version.Status);
    }

    [Fact]
    public void Parses_date_based_versions()
    {
        var group = ApiVersion.Parse("2026-01-15");
        Assert.Equal(new DateOnly(2026, 1, 15), group.GroupVersion);
        Assert.Null(group.MajorVersion);

        var combined = ApiVersion.Parse("2026-01-15.1.0-beta");
        Assert.Equal(new DateOnly(2026, 1, 15), combined.GroupVersion);
        Assert.Equal(1, combined.MajorVersion);
        Assert.Equal(0, combined.MinorVersion);
        Assert.Equal("beta", combined.Status);
        Assert.Equal("2026-01-15.1.0-beta", combined.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("1.0.0")]
    [InlineData("-1")]
    [InlineData("1.x")]
    [InlineData("1.0-")]
    [InlineData("1.0-be.ta")]
    [InlineData("2026-13-01")]
    [InlineData("latest")]
    [InlineData(" 1 0")]
    public void Rejects_what_is_not_a_version(string text)
    {
        Assert.False(ApiVersion.TryParse(text, out _));
        Assert.Throws<FormatException>(() => ApiVersion.Parse(text));
    }

    [Fact]
    public void A_missing_minor_equals_a_zero_minor()
    {
        Assert.Equal(ApiVersion.Parse("1"), ApiVersion.Parse("1.0"));
        Assert.Equal(ApiVersion.Parse("v1").GetHashCode(), ApiVersion.Parse("1.0").GetHashCode());
        Assert.Equal(new ApiVersion(1.0), new ApiVersion(1, 0));
        Assert.Equal(new ApiVersion(1, 1), new ApiVersion(1.1));
    }

    [Fact]
    public void Orders_releases_after_their_prereleases()
    {
        var ordered = new[] { "2.0", "1.0", "1.0-beta", "1.1", "2026-01-01" }
            .Select(ApiVersion.Parse)
            .Order()
            .Select(v => v.ToString())
            .ToArray();

        Assert.Equal(["1.0-beta", "1.0", "1.1", "2.0", "2026-01-01"], ordered);
        Assert.True(new ApiVersion(2, 0) > new ApiVersion(1, 9));
    }

    [Fact]
    public void Formats_short_and_full()
    {
        Assert.Equal("1.0", new ApiVersion(1, 0).ToString());
        Assert.Equal("1", new ApiVersion(1, 0).ToString("S"));
        Assert.Equal("1.1", new ApiVersion(1, 1).ToString("S"));
        Assert.Equal("2", new ApiVersion(2).ToString());
    }
}

public class ApiVersionRoutingTests
{
    [Theory]
    [InlineData("1.0", true)]
    [InlineData("v2", true)]
    [InlineData("2026-01-15", true)]
    [InlineData("users", false)]
    [InlineData("1.0.0", false)]
    public void The_apiVersion_constraint_matches_versions(string segment, bool matches)
        => Assert.Equal(matches, RouteConstraint.Parse("apiVersion")!.Matches(segment));

    [Fact]
    public void Both_template_parsers_accept_the_version_segment_and_reject_the_mixed_form()
    {
        Assert.NotNull(RouteTemplate.Parse("/api/{version:apiVersion}/users"));
        Assert.NotNull(RouteTemplateInfo.TryParse("/api/{version:apiVersion}/users", out _));

        var runtime = Assert.Throws<RouteTemplateException>(() => RouteTemplate.Parse("/api/v{version:apiVersion}/users"));
        Assert.Contains("{version:apiVersion}", runtime.Message);

        Assert.Null(RouteTemplateInfo.TryParse("/api/v{version:apiVersion}/users", out var error));
        Assert.Contains("apiVersion", error);
    }

    [Fact]
    public void Versions_of_one_route_coexist_in_the_table()
    {
        var server = new HttpServer();
        server.MapGet("/users", _ => default).HasApiVersion(1.0);
        server.MapGet("/users", _ => default).HasApiVersion(2.0);

        var match = server.Router.Match("GET", "/users", new RouteValueDictionary());

        Assert.Equal(2, match.Candidates.Count);
    }

    [Fact]
    public void Two_unversioned_endpoints_on_one_route_are_still_a_duplicate()
    {
        var server = new HttpServer();
        server.MapGet("/users", _ => default);

        Assert.Throws<InvalidOperationException>(() => server.MapGet("/users", _ => default));
    }

    [Fact]
    public void Two_endpoints_claiming_the_same_version_are_refused_at_registration()
    {
        var server = new HttpServer();
        server.Map("GET", "/users", _ => default, new ApiVersionMetadata { SupportedVersions = { new ApiVersion(1, 0) } });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            server.Map("GET", "/users", _ => default, new ApiVersionMetadata { SupportedVersions = { new ApiVersion(1) } }));

        Assert.Contains("1", ex.Message);
    }

    [Fact]
    public void A_versioned_endpoint_cannot_join_an_unversioned_route()
    {
        var server = new HttpServer();
        server.MapGet("/users", _ => default);

        Assert.Throws<InvalidOperationException>(() =>
            server.Map("GET", "/users", _ => default, new ApiVersionMetadata { SupportedVersions = { new ApiVersion(2, 0) } }));
    }
}

public class ApiVersioningEndToEndTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    sealed class Harness(IAsyncDisposable owner, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        /// <summary>
        /// A request at the client's own protocol version. A hand-built HttpRequestMessage defaults
        /// to HTTP/1.1 whatever the client says, which would quietly turn an HTTP/2 test into an
        /// HTTP/1.1 one.
        /// </summary>
        public HttpRequestMessage Request(string url) => new(HttpMethod.Get, url)
        {
            Version = this.Client.DefaultRequestVersion,
            VersionPolicy = this.Client.DefaultVersionPolicy
        };

        public async ValueTask DisposeAsync()
        {
            if (owner is not TestHttpServer)
                this.Client.Dispose();

            await owner.DisposeAsync();
        }
    }

    static async Task<Harness> StartAsync(
        VersioningTransport transport,
        Action<HttpServer> configure,
        Action<ApiVersioningOptions>? versioning = null
    )
    {
        void Builder(ShinyHttpServerBuilder b) => b.AddApiVersioning(versioning);

        switch (transport)
        {
            case VersioningTransport.InMemoryHttp1:
            case VersioningTransport.InMemoryHttp2:
            {
                var app = TestHttpServer.Create(configure, Builder, useHttp2: transport == VersioningTransport.InMemoryHttp2);
                return new Harness(app, app.Client);
            }

            default:
            {
                var server = await TestServer.StartAsync(configure, Builder);
                if (transport == VersioningTransport.Http1)
                    return new Harness(server, new HttpClient { BaseAddress = server.Client.BaseAddress });

                var client = new HttpClient(new SocketsHttpHandler())
                {
                    BaseAddress = server.Client.BaseAddress,
                    DefaultRequestVersion = HttpVersion.Version20,
                    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
                };

                return new Harness(server, client);
            }
        }
    }

    static void MapUsers(HttpServer app)
    {
        app.MapGet("/users", ctx => ctx.Response.WriteAsync("v1:" + ctx.GetRequestedApiVersion())).HasApiVersion(1.0);
        app.MapGet("/users", ctx => ctx.Response.WriteAsync("v2:" + ctx.GetRequestedApiVersion())).HasApiVersion(2.0);
    }

    static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement;
    }

    public static TheoryData<VersioningTransport> Transports => new()
    {
        VersioningTransport.Http1,
        VersioningTransport.Http2,
        VersioningTransport.InMemoryHttp1,
        VersioningTransport.InMemoryHttp2
    };

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Selects_between_versions_of_one_route_by_query_string(VersioningTransport transport)
    {
        await using var app = await StartAsync(transport, MapUsers);

        var v1 = await app.Client.GetAsync("/users?api-version=1.0", Token);

        // The protocol really is the one under test, not a silent fallback.
        Assert.Equal(
            transport is VersioningTransport.Http2 or VersioningTransport.InMemoryHttp2 ? HttpVersion.Version20 : HttpVersion.Version11,
            v1.Version
        );

        Assert.Equal("v1:1.0", await v1.Content.ReadAsStringAsync(Token));
        Assert.Equal("v2:2", await app.Client.GetStringAsync("/users?api-version=2", Token));
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Reads_the_version_from_a_header(VersioningTransport transport)
    {
        await using var app = await StartAsync(
            transport,
            MapUsers,
            o => o.ApiVersionReader = new HeaderApiVersionReader("api-version")
        );

        using var request = app.Request("/users");
        request.Headers.Add("api-version", "2.0");

        Assert.Equal("v2:2.0", await (await app.Client.SendAsync(request, Token)).Content.ReadAsStringAsync(Token));
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Reads_the_version_from_a_media_type_parameter(VersioningTransport transport)
    {
        await using var app = await StartAsync(
            transport,
            MapUsers,
            o => o.ApiVersionReader = new MediaTypeApiVersionReader("v")
        );

        using var request = app.Request("/users");
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/json; v=1.0"));

        Assert.Equal("v1:1.0", await (await app.Client.SendAsync(request, Token)).Content.ReadAsStringAsync(Token));
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Reads_the_version_from_a_url_segment(VersioningTransport transport)
    {
        await using var app = await StartAsync(transport, server =>
        {
            server.MapGet("/api/{version:apiVersion}/users", ctx => ctx.Response.WriteAsync("v1")).HasApiVersion(1.0);
            server.MapGet("/api/{version:apiVersion}/users", ctx => ctx.Response.WriteAsync("v2")).HasApiVersion(2.0);
        }, o => o.ApiVersionReader = new UrlSegmentApiVersionReader());

        Assert.Equal("v1", await app.Client.GetStringAsync("/api/v1/users", Token));
        Assert.Equal("v2", await app.Client.GetStringAsync("/api/2.0/users", Token));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/latest/users", Token)).StatusCode);

        var unsupported = await app.Client.GetAsync("/api/v3/users", Token);
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Combined_readers_that_agree_are_not_ambiguous(VersioningTransport transport)
    {
        await using var app = await StartAsync(transport, MapUsers, o => o.ApiVersionReader = ApiVersionReader.Combine(
            new UrlSegmentApiVersionReader(),
            new QueryStringApiVersionReader("api-version"),
            new HeaderApiVersionReader("api-version"),
            new MediaTypeApiVersionReader("v")
        ));

        using var request = app.Request("/users?api-version=2");
        request.Headers.Add("api-version", "2.0");

        Assert.Equal(HttpStatusCode.OK, (await app.Client.SendAsync(request, Token)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Readers_that_disagree_are_answered_ambiguous(VersioningTransport transport)
    {
        await using var app = await StartAsync(transport, MapUsers, o => o.ApiVersionReader = ApiVersionReader.Combine(
            new QueryStringApiVersionReader(),
            new HeaderApiVersionReader()
        ));

        using var request = app.Request("/users?api-version=1.0");
        request.Headers.Add("api-version", "2.0");

        var response = await app.Client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("AmbiguousApiVersion", (await ProblemAsync(response)).GetProperty("code").GetString());
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task An_unsupported_version_is_a_400_problem(VersioningTransport transport)
    {
        await using var app = await StartAsync(transport, MapUsers);

        var response = await app.Client.GetAsync("/users?api-version=3.0", Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal("UnsupportedApiVersion", problem.GetProperty("code").GetString());
        Assert.Equal("https://docs.api-versioning.org/problems#unsupported", problem.GetProperty("type").GetString());
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task The_unsupported_status_is_configurable()
    {
        await using var app = await StartAsync(
            VersioningTransport.InMemoryHttp1,
            MapUsers,
            o => o.UnsupportedApiVersionStatusCode = StatusCodes.Status404NotFound
        );

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/users?api-version=9", Token)).StatusCode);
    }

    [Fact]
    public async Task Text_that_is_not_a_version_is_an_invalid_version()
    {
        await using var app = await StartAsync(VersioningTransport.InMemoryHttp1, MapUsers);

        var response = await app.Client.GetAsync("/users?api-version=latest", Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("InvalidApiVersion", (await ProblemAsync(response)).GetProperty("code").GetString());
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task No_version_is_a_400_unless_a_default_is_assumed(VersioningTransport transport)
    {
        await using (var strict = await StartAsync(transport, MapUsers))
        {
            var response = await strict.Client.GetAsync("/users", Token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("ApiVersionUnspecified", (await ProblemAsync(response)).GetProperty("code").GetString());
        }

        await using var lenient = await StartAsync(transport, MapUsers, o =>
        {
            o.AssumeDefaultVersionWhenUnspecified = true;
            o.DefaultApiVersion = new ApiVersion(2, 0);
        });

        Assert.Equal("v2:2.0", await lenient.Client.GetStringAsync("/users", Token));
    }

    [Fact]
    public async Task The_current_implementation_selector_picks_the_newest_release()
    {
        await using var app = await StartAsync(VersioningTransport.InMemoryHttp1, server =>
        {
            MapUsers(server);
            server.MapGet("/users", ctx => ctx.Response.WriteAsync("v3-beta")).HasApiVersion("3.0-beta");
        }, o =>
        {
            o.AssumeDefaultVersionWhenUnspecified = true;
            o.ApiVersionSelector = new CurrentImplementationApiVersionSelector(o);
        });

        Assert.Equal("v2:2.0", await app.Client.GetStringAsync("/users", Token));
        Assert.Equal("v3-beta", await app.Client.GetStringAsync("/users?api-version=3.0-beta", Token));
    }

    [Fact]
    public async Task Unversioned_routes_are_untouched()
    {
        await using var app = await StartAsync(VersioningTransport.InMemoryHttp1, server =>
        {
            MapUsers(server);
            server.MapGet("/ping", ctx => ctx.Response.WriteAsync("pong"));
        }, o => o.ReportApiVersions = true);

        var response = await app.Client.GetAsync("/ping?api-version=banana", Token);

        Assert.Equal("pong", await response.Content.ReadAsStringAsync(Token));
        Assert.False(response.Headers.Contains(ApiVersionHeaderNames.SupportedVersions));
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Reports_supported_and_deprecated_versions(VersioningTransport transport)
    {
        await using var app = await StartAsync(transport, server =>
        {
            MapUsers(server);
            server.MapGet("/users", ctx => ctx.Response.WriteAsync("v0.9")).HasDeprecatedApiVersion(0.9);
        }, o => o.ReportApiVersions = true);

        foreach (var url in new[] { "/users?api-version=1.0", "/users?api-version=7.0" })
        {
            var response = await app.Client.GetAsync(url, Token);

            Assert.Equal("1.0, 2.0", string.Join(", ", response.Headers.GetValues(ApiVersionHeaderNames.SupportedVersions)));
            Assert.Equal("0.9", string.Join(", ", response.Headers.GetValues(ApiVersionHeaderNames.DeprecatedVersions)));
        }
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Emits_deprecation_sunset_and_link_headers_for_the_version_served(VersioningTransport transport)
    {
        var deprecated = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var sunset = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero);

        await using var app = await StartAsync(transport, MapUsers, o =>
        {
            o.Policies.Deprecate(1.0).Effective(deprecated).Link("https://example.com/deprecation", title: "Why");
            o.Policies.Sunset(1.0).Effective(sunset).Link("https://example.com/sunset");
        });

        var v1 = await app.Client.GetAsync("/users?api-version=1.0", Token);

        Assert.Equal("@" + deprecated.ToUnixTimeSeconds(), v1.Headers.GetValues("Deprecation").Single());
        Assert.Equal("Thu, 31 Dec 2026 23:59:59 GMT", v1.Headers.GetValues("Sunset").Single());

        var links = string.Join(",", v1.Headers.GetValues("Link"));
        Assert.Contains("<https://example.com/deprecation>; rel=\"deprecation\"; type=\"text/html\"; title=\"Why\"", links);
        Assert.Contains("<https://example.com/sunset>; rel=\"sunset\"", links);

        var v2 = await app.Client.GetAsync("/users?api-version=2.0", Token);
        Assert.False(v2.Headers.Contains("Deprecation"));
        Assert.False(v2.Headers.Contains("Sunset"));
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Versions_a_route_group(VersioningTransport transport)
    {
        await using var app = await StartAsync(transport, server => server.MapGroup("/api/{version:apiVersion}", api =>
        {
            var v1 = api.HasApiVersion(1.0);
            v1.MapGet("/users", ctx => ctx.Response.WriteAsync("group-v1"));

            var v2 = api.HasApiVersion(2.0);
            v2.MapGet("/users", ctx => ctx.Response.WriteAsync("group-v2"));
            v2.MapGroup("/admin").MapGet("/audit", ctx => ctx.Response.WriteAsync("audit-v2"));
        }), o => o.ApiVersionReader = new UrlSegmentApiVersionReader());

        Assert.Equal("group-v1", await app.Client.GetStringAsync("/api/v1/users", Token));
        Assert.Equal("group-v2", await app.Client.GetStringAsync("/api/v2/users", Token));
        Assert.Equal("audit-v2", await app.Client.GetStringAsync("/api/v2/admin/audit", Token));
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync("/api/v1/admin/audit", Token)).StatusCode);
    }

    [Fact]
    public async Task Maps_routes_of_a_version_set_to_its_versions()
    {
        await using var app = await StartAsync(VersioningTransport.InMemoryHttp1, server =>
        {
            var set = server.NewApiVersionSet("weather")
                .HasDeprecatedApiVersion(0.9)
                .HasApiVersion(1.0)
                .HasApiVersion(2.0)
                .Build();

            server.MapGet("/weather", ctx => ctx.Response.WriteAsync("old")).WithApiVersionSet(set);
            server.MapGet("/weather", ctx => ctx.Response.WriteAsync("new")).WithApiVersionSet(set).MapToApiVersion(2.0);
        }, o => o.ReportApiVersions = true);

        Assert.Equal("old", await app.Client.GetStringAsync("/weather?api-version=0.9", Token));
        Assert.Equal("old", await app.Client.GetStringAsync("/weather?api-version=1.0", Token));

        var v2 = await app.Client.GetAsync("/weather?api-version=2.0", Token);
        Assert.Equal("new", await v2.Content.ReadAsStringAsync(Token));
        Assert.Equal("1.0, 2.0", v2.Headers.GetValues(ApiVersionHeaderNames.SupportedVersions).Single());
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Dispatches_generated_endpoints_by_version(VersioningTransport transport)
    {
        await using var app = await StartAsync(transport, server =>
        {
            server.MapVersionedUserEndpoints();
            server.MapVersionedOrderEndpoints();
            server.MapVersionNeutralEndpoints();
        }, o =>
        {
            o.ReportApiVersions = true;
            o.ApiVersionReader = ApiVersionReader.Combine(new QueryStringApiVersionReader(), new UrlSegmentApiVersionReader());
        });

        Assert.Equal("users@0.9", await app.Client.GetStringAsync("/versioned-gen/users?api-version=0.9", Token));
        Assert.Equal("users@1.0", await app.Client.GetStringAsync("/versioned-gen/users?api-version=1.0", Token));
        Assert.Equal("users-v2", await app.Client.GetStringAsync("/versioned-gen/users?api-version=2.0", Token));

        var reported = await app.Client.GetAsync("/versioned-gen/users?api-version=1", Token);
        Assert.Equal("1.0, 2.0", reported.Headers.GetValues(ApiVersionHeaderNames.SupportedVersions).Single());
        Assert.Equal("0.9", reported.Headers.GetValues(ApiVersionHeaderNames.DeprecatedVersions).Single());

        Assert.Equal("order-v1-5", await app.Client.GetStringAsync("/versioned-gen/v1/orders/5", Token));
        Assert.Equal("order-v2-5", await app.Client.GetStringAsync("/versioned-gen/v2.0/orders/5", Token));

        Assert.Equal("healthy", await app.Client.GetStringAsync("/versioned-gen/health", Token));
        Assert.Equal("healthy", await app.Client.GetStringAsync("/versioned-gen/health?api-version=42.0", Token));
    }

    [Fact]
    public async Task Works_without_a_container()
    {
        await using var server = new HttpServer(new HttpServerOptions { Port = 0 });
        server.UseApiVersioning(o => o.AssumeDefaultVersionWhenUnspecified = true);
        MapUsers(server);

        using var client = server.CreateInMemoryClient();

        Assert.Equal("v1:1.0", await client.GetStringAsync("/users", Token));
        Assert.Equal("v2:2.0", await client.GetStringAsync("/users?api-version=2.0", Token));
    }

    [Fact]
    public async Task Versioning_works_with_no_registration_at_all()
    {
        await using var server = new HttpServer(new HttpServerOptions { Port = 0 });
        MapUsers(server);

        using var client = server.CreateInMemoryClient();

        Assert.Equal("v2:2.0", await client.GetStringAsync("/users?api-version=2.0", Token));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/users", Token)).StatusCode);
    }

    [Fact]
    public async Task Pre_routing_policies_see_the_selected_version()
    {
        // CORS, IP filtering and rate limiting resolve the endpoint before routing does. On a route
        // with several versions they must see the one the request will actually reach.
        Endpoint? seen = null;

        await using var app = await StartAsync(VersioningTransport.InMemoryHttp1, server =>
        {
            server.MapGet("/users", ctx => ctx.Response.WriteAsync("v1")).HasApiVersion(1.0);
            server.MapRoute(
                "GET",
                "/users",
                ctx => ctx.Response.WriteAsync("v2"),
                new ApiVersionMetadata { SupportedVersions = { new ApiVersion(2, 0) } },
                "marker-v2"
            );
            server.Use(async (ctx, next) =>
            {
                seen = Internal.EndpointResolver.Resolve(server.Router, ctx);
                await next(ctx);
            });
        });

        Assert.Equal("v2", await app.Client.GetStringAsync("/users?api-version=2.0", Token));
        Assert.Equal("marker-v2", seen?.GetMetadata<string>());
    }

    [Fact]
    public void The_generator_refuses_nothing_about_the_versioned_classes()
    {
        // The generator reports duplicates and bad versions at compile time; that this test project
        // compiled with VersionedUserEndpoints (implicit 1.0/0.9 beside an explicit 2.0) and
        // VersionedOrderEndpoints (two methods on one route) is the assertion. What remains to check
        // is the metadata it emitted.
        var server = new HttpServer();
        server.MapVersionedUserEndpoints();

        var versions = server.Router.Endpoints.Select(e => e.GetMetadata<ApiVersionMetadata>()!.ToString()).ToArray();

        Assert.Equal(["0.9, 1.0, 2.0", "2.0"], versions);
    }
}

public class ApiVersioningOpenApiTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static HttpServer Server()
    {
        var server = new HttpServer(new HttpServerOptions { Port = 0 });
        server.UseApiVersioning(o => o.Policies.Deprecate(0.9));
        server.MapVersionedUserEndpoints();
        server.MapVersionedOrderEndpoints();
        server.MapVersionNeutralEndpoints();
        server.MapGet("/ping", ctx => ctx.Response.WriteAsync("pong"));
        return server;
    }

    [Fact]
    public void Lists_every_version_served()
        => Assert.Equal(
            ["0.9", "1.0", "2.0"],
            OpenApiDocumentBuilder.GetApiVersions(Server()).Select(v => v.ToString())
        );

    [Fact]
    public void Builds_one_document_per_version()
    {
        var server = Server();

        var v1 = JsonDocument.Parse(OpenApiDocumentBuilder.BuildJson(server, new OpenApiOptions(), new ApiVersion(1, 0))).RootElement;
        var v2 = JsonDocument.Parse(OpenApiDocumentBuilder.BuildJson(server, new OpenApiOptions(), new ApiVersion(2, 0))).RootElement;

        Assert.Equal("1.0", v1.GetProperty("info").GetProperty("version").GetString());
        Assert.Equal("Lists users the old way.", v1.GetProperty("paths").GetProperty("/versioned-gen/users").GetProperty("get").GetProperty("summary").GetString());
        Assert.Equal("Lists users the new way.", v2.GetProperty("paths").GetProperty("/versioned-gen/users").GetProperty("get").GetProperty("summary").GetString());

        // The version segment is written as the version, with no parameter left to describe.
        var order = v2.GetProperty("paths").GetProperty("/versioned-gen/v2/orders/{id}").GetProperty("get");
        Assert.DoesNotContain(
            order.GetProperty("parameters").EnumerateArray(),
            p => p.GetProperty("name").GetString() == "version"
        );
        Assert.False(v2.GetProperty("paths").TryGetProperty("/versioned-gen/v1/orders/{id}", out _));

        // Unversioned and neutral routes belong to every version.
        Assert.True(v1.GetProperty("paths").TryGetProperty("/ping", out _));
        Assert.True(v2.GetProperty("paths").TryGetProperty("/versioned-gen/health", out _));
    }

    [Fact]
    public void Flags_deprecated_operations_in_their_versions_document()
    {
        var server = Server();

        var old = JsonDocument.Parse(OpenApiDocumentBuilder.BuildJson(server, new OpenApiOptions(), new ApiVersion(0, 9))).RootElement;
        var current = JsonDocument.Parse(OpenApiDocumentBuilder.BuildJson(server, new OpenApiOptions(), new ApiVersion(1, 0))).RootElement;

        Assert.True(old.GetProperty("paths").GetProperty("/versioned-gen/users").GetProperty("get").GetProperty("deprecated").GetBoolean());
        Assert.Contains("deprecated", old.GetProperty("info").GetProperty("description").GetString());

        // The same endpoint, shared between the two documents, is not deprecated at 1.0.
        Assert.False(current.GetProperty("paths").GetProperty("/versioned-gen/users").GetProperty("get").TryGetProperty("deprecated", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Serves_a_document_per_version_by_name(bool http2)
    {
        var server = Server();
        server.MapOpenApi("/openapi/{documentName}.json");

        await using var app = TestHttpServer.For(server, useHttp2: http2);

        var v1 = JsonDocument.Parse(await app.Client.GetStringAsync("/openapi/v1.json", Token)).RootElement;
        var v2 = JsonDocument.Parse(await app.Client.GetStringAsync("/openapi/2.0.json", Token)).RootElement;

        Assert.Equal("1.0", v1.GetProperty("info").GetProperty("version").GetString());
        Assert.Equal("2.0", v2.GetProperty("info").GetProperty("version").GetString());
        Assert.False(v1.GetProperty("paths").TryGetProperty("/openapi/{documentName}", out _));

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/openapi/v7.json", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/openapi/v1.yaml", Token)).StatusCode);
    }

    [Fact]
    public void One_fixed_version_can_be_chosen_through_the_options()
    {
        var document = JsonDocument.Parse(
            OpenApiDocumentBuilder.BuildJson(Server(), new OpenApiOptions { ApiVersion = new ApiVersion(2, 0) })
        ).RootElement;

        Assert.Equal("2.0", document.GetProperty("info").GetProperty("version").GetString());
    }
}

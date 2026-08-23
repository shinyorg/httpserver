using Microsoft.Extensions.DependencyInjection;

namespace Shiny.Net.HttpServer.Testing;

/// <summary>
/// A server and a client wired to each other, for a test that wants one line of setup.
/// <code>
/// await using var app = TestHttpServer.Create(server =>
///     server.MapGet("/users/{id:int}", ctx => ctx.Response.WriteTextAsync(ctx.Request.RouteValues["id"]!)));
///
/// Assert.Equal("42", await app.Client.GetStringAsync("/users/42"));
/// </code>
/// <para>
/// Registering services works exactly as it does in the app, because it is the same builder:
/// <code>
/// await using var app = TestHttpServer.Create(
///     server => server.MapMyAppEndpoints(),
///     builder => builder.Services.AddSingleton&lt;IClock&gt;(new FrozenClock(...))
/// );
/// </code>
/// </para>
/// </summary>
public sealed class TestHttpServer : IAsyncDisposable
{
    TestHttpServer(HttpServer server, HttpClient client)
    {
        this.Server = server;
        this.Client = client;
    }

    /// <summary>The server under test. Its routes, middleware and container are all real.</summary>
    public HttpServer Server { get; }

    /// <summary>A client whose requests go to <see cref="Server"/> through memory.</summary>
    public HttpClient Client { get; }

    /// <summary>Everything the server was built with, for a test that needs to reach into it.</summary>
    public IServiceProvider Services => this.Server.Services
        ?? throw new InvalidOperationException("This server was built without a container.");

    /// <summary>Builds a server, configures it, and hands back a client pointed at it.</summary>
    /// <param name="configure">Routes, middleware and anything else that goes on the server.</param>
    /// <param name="configureBuilder">Services, options and logging.</param>
    /// <param name="useHttp2">Speaks HTTP/2 by prior knowledge. Needs <c>Http2.AllowCleartext</c>.</param>
    public static TestHttpServer Create(
        Action<HttpServer> configure,
        Action<ShinyHttpServerBuilder>? configureBuilder = null,
        bool useHttp2 = false
    )
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = HttpServer.CreateBuilder();

        // Nothing is bound, so the port never matters — but a test that later starts the server
        // for real should not fight over a fixed one.
        builder.Options.Port = 0;

        // A test wants the exception, not the sanitised 500 a production server answers with.
        builder.Options.HideExceptionDetails = false;

        if (useHttp2)
            builder.Options.Http2.AllowCleartext = true;

        configureBuilder?.Invoke(builder);

        var server = builder.Build();
        configure(server);

        return new TestHttpServer(server, server.CreateInMemoryClient(useHttp2: useHttp2));
    }

    /// <summary>Wraps a server that was built elsewhere.</summary>
    public static TestHttpServer For(HttpServer server, bool useHttp2 = false)
    {
        ArgumentNullException.ThrowIfNull(server);

        return new TestHttpServer(server, server.CreateInMemoryClient(useHttp2: useHttp2));
    }

    /// <summary>Another client to the same server — a second "device", with its own connections and cookies.</summary>
    public HttpClient CreateClient(bool useHttp2 = false) => this.Server.CreateInMemoryClient(useHttp2: useHttp2);

    public async ValueTask DisposeAsync()
    {
        this.Client.Dispose();
        await this.Server.DisposeAsync().ConfigureAwait(false);
    }
}

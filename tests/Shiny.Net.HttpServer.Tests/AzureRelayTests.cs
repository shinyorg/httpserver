using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using Shiny.Net.HttpServer.AzureRelay;
using Shiny.Net.HttpServer.Transports;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// Everything here is the Azure-free half of the provider: response parsing, option validation,
/// SAS minting and path rewriting. Anything that needs a live relay namespace is verified by hand
/// against a real one — see the package README.
/// </summary>
public class AzureRelayResponseReaderTests
{
    static PipeReader Wire(string text)
    {
        var pipe = new Pipe();
        pipe.Writer.Write(Encoding.Latin1.GetBytes(text));
        pipe.Writer.Complete();

        return pipe.Reader;
    }

    [Fact]
    public async Task Reads_a_counted_body()
    {
        var response = await Http1ResponseReader.ReadAsync(
            Wire("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 5\r\n\r\nhello"),
            64 * 1024,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("OK", response.ReasonPhrase);
        Assert.Equal("hello", Encoding.UTF8.GetString(response.Body!));
        Assert.Contains(response.Headers, h => h is { Key: "Content-Type", Value: "text/plain" });
    }

    [Fact]
    public async Task Reads_a_chunked_body()
    {
        var response = await Http1ResponseReader.ReadAsync(
            Wire("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n"),
            64 * 1024,
            TestContext.Current.CancellationToken
        );

        Assert.Equal("hello world", Encoding.UTF8.GetString(response.Body!));
    }

    [Fact]
    public async Task Ignores_chunk_extensions_and_trailers()
    {
        var response = await Http1ResponseReader.ReadAsync(
            Wire("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5;name=value\r\nhello\r\n0\r\nX-Trailer: 1\r\n\r\n"),
            64 * 1024,
            TestContext.Current.CancellationToken
        );

        Assert.Equal("hello", Encoding.UTF8.GetString(response.Body!));
    }

    [Fact]
    public async Task Reports_no_body_for_a_bodiless_response()
    {
        var response = await Http1ResponseReader.ReadAsync(
            Wire("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n"),
            64 * 1024,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(204, response.StatusCode);
        Assert.Null(response.Body);
    }

    /// <summary>
    /// The head can straddle reads, which is the normal case on a real pipe — the parser has to
    /// wait rather than treat a partial head as malformed.
    /// </summary>
    [Fact]
    public async Task Waits_for_a_head_split_across_reads()
    {
        var pipe = new Pipe();
        var reading = Http1ResponseReader
            .ReadAsync(pipe.Reader, 64 * 1024, TestContext.Current.CancellationToken)
            .AsTask();

        pipe.Writer.Write("HTTP/1.1 201 Created\r\nLoca"u8);
        await pipe.Writer.FlushAsync(TestContext.Current.CancellationToken);

        Assert.False(reading.IsCompleted);

        pipe.Writer.Write("tion: /things/1\r\nContent-Length: 2\r\n\r\nok"u8);
        await pipe.Writer.FlushAsync(TestContext.Current.CancellationToken);
        await pipe.Writer.CompleteAsync();

        var response = await reading;

        Assert.Equal(201, response.StatusCode);
        Assert.Contains(response.Headers, h => h is { Key: "Location", Value: "/things/1" });
        Assert.Equal("ok", Encoding.UTF8.GetString(response.Body!));
    }

    /// <summary>Without a declared length the body runs to end of stream, which is what a connection close means.</summary>
    [Fact]
    public async Task Reads_to_end_of_stream_when_no_length_is_declared()
    {
        var response = await Http1ResponseReader.ReadAsync(
            Wire("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n\r\nunbounded"),
            64 * 1024,
            TestContext.Current.CancellationToken
        );

        Assert.Equal("unbounded", Encoding.UTF8.GetString(response.Body!));
    }

    [Fact]
    public async Task Rejects_a_head_larger_than_the_limit()
    {
        var big = "HTTP/1.1 200 OK\r\nX-Big: " + new string('a', 4096) + "\r\n";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Http1ResponseReader.ReadAsync(Wire(big), 512, TestContext.Current.CancellationToken).AsTask()
        );
    }

    [Fact]
    public async Task Rejects_a_truncated_head()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Http1ResponseReader
                .ReadAsync(Wire("HTTP/1.1 200 OK\r\nContent-Len"), 64 * 1024, TestContext.Current.CancellationToken)
                .AsTask()
        );
    }
}

public class AzureRelayOptionsTests
{
    [Fact]
    public void Resolves_a_connection_string_with_an_entity_path()
    {
        var options = new AzureRelayOptions
        {
            ConnectionString =
                "Endpoint=sb://contoso.servicebus.windows.net/;SharedAccessKeyName=listen;SharedAccessKey=abc;EntityPath=device-1"
        };

        var (host, name) = options.Resolve();

        Assert.Equal("contoso.servicebus.windows.net", host);
        Assert.Equal("device-1", name);
    }

    [Fact]
    public void Requires_a_connection_name_when_the_connection_string_omits_one()
    {
        var options = new AzureRelayOptions
        {
            ConnectionString = "Endpoint=sb://contoso.servicebus.windows.net/;SharedAccessKeyName=listen;SharedAccessKey=abc"
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Resolve());
        Assert.Contains(nameof(AzureRelayOptions.HybridConnectionName), ex.Message);
    }

    [Fact]
    public void Requires_credentials()
    {
        var options = new AzureRelayOptions
        {
            Namespace = "contoso.servicebus.windows.net",
            HybridConnectionName = "device-1"
        };

        Assert.Throws<InvalidOperationException>(() => options.Resolve());
    }

    [Fact]
    public void Accepts_a_bare_signature_without_a_key()
    {
        var options = new AzureRelayOptions
        {
            Namespace = "contoso.servicebus.windows.net",
            HybridConnectionName = "device-1",
            SharedAccessSignature = "SharedAccessSignature sr=x&sig=y&se=1&skn=z"
        };

        var (host, name) = options.Resolve();

        Assert.Equal("contoso.servicebus.windows.net", host);
        Assert.Equal("device-1", name);
    }

    [Fact]
    public void Reports_a_public_https_url_in_http_mode()
    {
        var provider = new AzureRelayTunnelProvider(new AzureRelayOptions
        {
            Namespace = "contoso.servicebus.windows.net",
            HybridConnectionName = "device-1",
            SharedAccessSignature = "SharedAccessSignature sr=x&sig=y&se=1&skn=z"
        });

        Assert.Equal("https://contoso.servicebus.windows.net/device-1", provider.PublicUrl);
    }

    [Fact]
    public void Reports_an_sb_url_in_relayed_stream_mode()
    {
        var provider = new AzureRelayTunnelProvider(new AzureRelayOptions
        {
            Namespace = "contoso.servicebus.windows.net",
            HybridConnectionName = "device-1",
            SharedAccessSignature = "SharedAccessSignature sr=x&sig=y&se=1&skn=z",
            Mode = AzureRelayMode.RelayedStream
        });

        Assert.Equal("sb://contoso.servicebus.windows.net/device-1", provider.PublicUrl);
    }

    /// <summary>
    /// The whole point of stripping: the same route serves local and relayed traffic, so an app does
    /// not have to know its hybrid connection name when it maps a handler.
    /// </summary>
    [Theory]
    [InlineData("https://contoso.servicebus.windows.net/device-1/api/widgets", "/api/widgets")]
    [InlineData("https://contoso.servicebus.windows.net/device-1", "/")]
    [InlineData("https://contoso.servicebus.windows.net/device-1/", "/")]
    [InlineData("https://contoso.servicebus.windows.net/device-1/api/widgets?take=5", "/api/widgets?take=5")]
    [InlineData("https://contoso.servicebus.windows.net/DEVICE-1/api/widgets", "/api/widgets")]
    public void Strips_the_connection_name_from_the_path(string url, string expected)
    {
        var provider = Provider(strip: true);

        Assert.Equal(expected, provider.BuildTarget(new Uri(url)));
    }

    [Fact]
    public void Keeps_the_connection_name_when_stripping_is_off()
    {
        var provider = Provider(strip: false);

        Assert.Equal(
            "/device-1/api/widgets",
            provider.BuildTarget(new Uri("https://contoso.servicebus.windows.net/device-1/api/widgets"))
        );
    }

    static AzureRelayTunnelProvider Provider(bool strip) => new(new AzureRelayOptions
    {
        Namespace = "contoso.servicebus.windows.net",
        HybridConnectionName = "device-1",
        SharedAccessSignature = "SharedAccessSignature sr=x&sig=y&se=1&skn=z",
        StripHybridConnectionNameFromPath = strip
    });
}

/// <summary>
/// The HTTP-mode bridge, driven against a real server. No relay namespace is involved: the halves
/// that talk to Azure are thin, and the half that decides framing is the one that breaks.
/// </summary>
public class AzureRelayHttpBridgeTests
{
    static AzureRelayTunnelProvider Provider() => new(new AzureRelayOptions
    {
        Namespace = "contoso.servicebus.windows.net",
        HybridConnectionName = "device-1",
        SharedAccessSignature = "SharedAccessSignature sr=x&sig=y&se=1&skn=z"
    });

    static async Task<Http1Response> RelayAsync(
        HttpServer server,
        string method,
        string url,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null,
        Stream? body = null
    )
    {
        var provider = Provider();
        var connection = new DuplexPipeConnection("test", isTunneled: true);
        var ct = TestContext.Current.CancellationToken;

        var serving = server.ServeAsync(connection, ct);

        await provider.WriteRequestAsync(method, new Uri(url), headers ?? [], body, connection.TransportWriter, ct);

        var response = await Http1ResponseReader.ReadAsync(connection.TransportReader, 64 * 1024, ct);
        await serving;

        return response;
    }

    [Fact]
    public async Task Relays_a_get_to_the_route_the_app_mapped()
    {
        var server = new HttpServer();
        server.MapGet("/api/widgets", ctx => ctx.Response.WriteTextAsync("two widgets"));

        var response = await RelayAsync(server, "GET", "https://contoso.servicebus.windows.net/device-1/api/widgets");

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("two widgets", Encoding.UTF8.GetString(response.Body!));
    }

    [Fact]
    public async Task Passes_the_query_string_through()
    {
        var server = new HttpServer();
        server.MapGet("/search", ctx => ctx.Response.WriteTextAsync(ctx.Request.Query["q"].ToString()));

        var response = await RelayAsync(server, "GET", "https://contoso.servicebus.windows.net/device-1/search?q=hello");

        Assert.Equal("hello", Encoding.UTF8.GetString(response.Body!));
    }

    [Fact]
    public async Task Passes_caller_headers_through()
    {
        var server = new HttpServer();
        server.MapGet("/echo", ctx => ctx.Response.WriteTextAsync(ctx.Request.Headers["Authorization"].ToString()));

        var response = await RelayAsync(
            server,
            "GET",
            "https://contoso.servicebus.windows.net/device-1/echo",
            [new KeyValuePair<string, string>("Authorization", "Bearer abc")]
        );

        Assert.Equal("Bearer abc", Encoding.UTF8.GetString(response.Body!));
    }

    /// <summary>
    /// A relayed body arrives as a stream of unknown length, so it is chunked onto the wire. The
    /// server has to see the same bytes either way.
    /// </summary>
    [Fact]
    public async Task Chunks_a_body_of_unknown_length()
    {
        var server = new HttpServer();
        server.MapPost("/upload", async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var text = await reader.ReadToEndAsync(ctx.RequestAborted);

            await ctx.Response.WriteTextAsync($"got {text.Length}: {text}");
        });

        var payload = new string('x', 40_000);
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        var response = await RelayAsync(
            server,
            "POST",
            "https://contoso.servicebus.windows.net/device-1/upload",
            [new KeyValuePair<string, string>("Content-Type", "text/plain")],
            body
        );

        Assert.Equal(200, response.StatusCode);
        Assert.Equal($"got {payload.Length}: {payload}", Encoding.UTF8.GetString(response.Body!));
    }

    [Fact]
    public async Task Passes_a_declared_content_length_through()
    {
        var server = new HttpServer();
        server.MapPost("/upload", async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var text = await reader.ReadToEndAsync(ctx.RequestAborted);

            await ctx.Response.WriteTextAsync($"{ctx.Request.ContentLength}:{text}");
        });

        using var body = new MemoryStream("hello"u8.ToArray());

        var response = await RelayAsync(
            server,
            "POST",
            "https://contoso.servicebus.windows.net/device-1/upload",
            [new KeyValuePair<string, string>("Content-Length", "5")],
            body
        );

        Assert.Equal("5:hello", Encoding.UTF8.GetString(response.Body!));
    }

    /// <summary>
    /// The relay frames the request itself, so its hop-by-hop headers describe a leg that has
    /// already ended. Forwarding them would give the server two contradictory descriptions of the
    /// same body.
    /// </summary>
    [Fact]
    public async Task Drops_hop_by_hop_headers_from_the_relay()
    {
        var server = new HttpServer();
        server.MapGet("/headers", ctx => ctx.Response.WriteTextAsync(
            string.Join(",", ctx.Request.Headers.Select(h => h.Key).Order(StringComparer.OrdinalIgnoreCase))
        ));

        var response = await RelayAsync(
            server,
            "GET",
            "https://contoso.servicebus.windows.net/device-1/headers",
            [
                new KeyValuePair<string, string>("Connection", "keep-alive"),
                new KeyValuePair<string, string>("Transfer-Encoding", "chunked"),
                new KeyValuePair<string, string>("Upgrade", "h2c"),
                new KeyValuePair<string, string>("X-Kept", "yes")
            ]
        );

        var names = Encoding.UTF8.GetString(response.Body!);

        Assert.Contains("X-Kept", names, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Transfer-Encoding", names, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Upgrade", names, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Azure's URL has no Host header of its own to offer, so one is synthesised.</summary>
    [Fact]
    public async Task Supplies_a_host_header_when_the_relay_omits_one()
    {
        var server = new HttpServer();
        server.MapGet("/host", ctx => ctx.Response.WriteTextAsync(ctx.Request.Headers["Host"].ToString()));

        var response = await RelayAsync(server, "GET", "https://contoso.servicebus.windows.net/device-1/host");

        Assert.Equal("contoso.servicebus.windows.net", Encoding.UTF8.GetString(response.Body!));
    }

    [Fact]
    public async Task Relays_a_status_code_and_headers_from_a_result()
    {
        var server = new HttpServer();
        server.MapGet("/missing", _ => Results.NotFound());

        var response = await RelayAsync(server, "GET", "https://contoso.servicebus.windows.net/device-1/missing");

        Assert.Equal(404, response.StatusCode);
    }
}

public class AzureRelaySasTests
{
    const string Key = "3Wo0PJd1Q0Y6nGZ3iVRLbGm0F0R8p1qEXAMPLEKEY=";

    [Fact]
    public void Creates_a_token_azure_will_accept()
    {
        var token = AzureRelaySas.Create(
            "contoso.servicebus.windows.net",
            "device-1",
            "listen",
            Key,
            TimeSpan.FromHours(1)
        );

        Assert.StartsWith("SharedAccessSignature ", token);

        var fields = token["SharedAccessSignature ".Length..]
            .Split('&')
            .Select(part => part.Split('=', 2))
            .ToDictionary(part => part[0], part => part[1]);

        var resource = Uri.EscapeDataString("http://contoso.servicebus.windows.net/device-1");

        Assert.Equal(resource, fields["sr"]);
        Assert.Equal("listen", fields["skn"]);

        // Recompute the signature the way the service does, so a change to the string-to-sign
        // cannot slip through as a token that merely looks well-formed.
        var expected = Convert.ToBase64String(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(Key), Encoding.UTF8.GetBytes($"{resource}\n{fields["se"]}"))
        );

        Assert.Equal(expected, Uri.UnescapeDataString(fields["sig"]));
    }

    [Fact]
    public void Signs_the_lowercased_resource()
    {
        var mixed = AzureRelaySas.Create("Contoso.ServiceBus.Windows.Net", "Device-1", "listen", Key, TimeSpan.FromHours(1));

        Assert.Contains(Uri.EscapeDataString("http://contoso.servicebus.windows.net/device-1"), mixed);
    }

    [Fact]
    public void Round_trips_the_expiry()
    {
        var token = AzureRelaySas.Create("contoso.servicebus.windows.net", "device-1", "listen", Key, TimeSpan.FromHours(2));
        var expiry = AzureRelaySas.GetExpiry(token);

        Assert.NotNull(expiry);
        Assert.InRange(
            expiry.Value - DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(119),
            TimeSpan.FromMinutes(121)
        );
    }

    [Fact]
    public void Returns_no_expiry_for_a_token_it_cannot_parse()
    {
        Assert.Null(AzureRelaySas.GetExpiry("not-a-token"));
        Assert.Null(AzureRelaySas.GetExpiry(""));
    }

    [Fact]
    public void Refuses_to_mint_an_expired_token()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AzureRelaySas.Create("contoso.servicebus.windows.net", "device-1", "listen", Key, TimeSpan.FromHours(-1))
        );
    }

    [Fact]
    public void Requires_every_part()
    {
        Assert.Throws<ArgumentException>(() =>
            AzureRelaySas.Create("contoso.servicebus.windows.net", "device-1", "listen", "", TimeSpan.FromHours(1))
        );

        Assert.Throws<ArgumentException>(() =>
            AzureRelaySas.Create("contoso.servicebus.windows.net", "", "listen", Key, TimeSpan.FromHours(1))
        );
    }

    [Fact]
    public void Expiry_is_seconds_since_the_epoch()
    {
        var token = AzureRelaySas.Create("contoso.servicebus.windows.net", "device-1", "listen", Key, TimeSpan.FromMinutes(30));
        var se = token.Split('&').First(part => part.StartsWith("se=", StringComparison.Ordinal))[3..];

        Assert.True(long.TryParse(se, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds));
        Assert.InRange(seconds, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), DateTimeOffset.UtcNow.AddMinutes(31).ToUnixTimeSeconds());
    }
}

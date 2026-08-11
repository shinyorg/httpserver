using System.Buffers;
using System.Net;
using System.Text;
using Shiny.Net.HttpServer.Http2.Hpack;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The Huffman table is transcribed from RFC 7541 Appendix B, so it is checked structurally as well
/// as behaviourally. A wrong entry that happens to stay prefix-free would round-trip against itself
/// and still fail against every real client — which is what the interop tests are for.
/// </summary>
public class HpackHuffmanTests
{
    [Fact]
    public void Round_trips_every_byte_value()
    {
        for (var i = 0; i < 256; i++)
        {
            var source = new[] { (byte)i };
            var encoded = new byte[16];
            var encodedLength = HpackHuffman.Encode(source, encoded);

            var decoded = new byte[16];
            var decodedLength = HpackHuffman.Decode(encoded.AsSpan(0, encodedLength), decoded);

            Assert.Equal(1, decodedLength);
            Assert.Equal((byte)i, decoded[0]);
        }
    }

    [Fact]
    public void Round_trips_arbitrary_data()
    {
        var source = new byte[4096];
        Random.Shared.NextBytes(source);

        var encoded = new byte[HpackHuffman.GetEncodedLength(source)];
        var encodedLength = HpackHuffman.Encode(source, encoded);

        var decoded = new byte[HpackHuffman.GetMaxDecodedLength(encodedLength)];
        var decodedLength = HpackHuffman.Decode(encoded.AsSpan(0, encodedLength), decoded);

        Assert.Equal(source, decoded.AsSpan(0, decodedLength).ToArray());
    }

    [Theory]
    // The worked examples from RFC 7541 Appendix C.
    [InlineData("www.example.com", "f1e3c2e5f23a6ba0ab90f4ff")]
    [InlineData("no-cache", "a8eb10649cbf")]
    [InlineData("custom-key", "25a849e95ba97d7f")]
    [InlineData("custom-value", "25a849e95bb8e8b4bf")]
    public void Matches_the_rfc_test_vectors(string text, string expectedHex)
    {
        var source = Encoding.ASCII.GetBytes(text);
        var encoded = new byte[HpackHuffman.GetEncodedLength(source)];
        var length = HpackHuffman.Encode(source, encoded);

        Assert.Equal(expectedHex, Convert.ToHexString(encoded.AsSpan(0, length)).ToLowerInvariant());

        var decoded = new byte[64];
        var decodedLength = HpackHuffman.Decode(Convert.FromHexString(expectedHex), decoded);

        Assert.Equal(text, Encoding.ASCII.GetString(decoded, 0, decodedLength));
    }

    [Fact]
    public void The_code_is_complete_and_prefix_free()
    {
        // Kraft equality: for a complete prefix code the lengths must sum to exactly 1 when each
        // contributes 2^-length. Any slip in the transcribed lengths breaks this, and the decoding
        // tree would already have refused to build if a code collided.
        var total = 0d;

        for (var symbol = 0; symbol <= HpackHuffman.EndOfString; symbol++)
            total += Math.Pow(2, -HpackHuffman.GetCodeLength(symbol));

        Assert.Equal(1d, total, precision: 12);
    }
}

public class HpackCodecTests
{
    static List<HeaderField> Decode(string hex, HpackDecoder? decoder = null)
    {
        var fields = new List<HeaderField>();
        (decoder ?? new HpackDecoder()).Decode(Convert.FromHexString(hex), fields);

        return fields;
    }

    [Fact]
    public void Decodes_an_indexed_static_entry()
    {
        // 0x82 = indexed field, index 2 = ":method: GET".
        var fields = Decode("82");

        Assert.Single(fields);
        Assert.Equal(":method", fields[0].Name);
        Assert.Equal("GET", fields[0].Value);
    }

    [Fact]
    public void Decodes_the_rfc_literal_example()
    {
        // RFC 7541 C.2.1: a literal with an incremental-indexing name and value.
        var fields = Decode("400a637573746f6d2d6b65790d637573746f6d2d686561646572");

        Assert.Single(fields);
        Assert.Equal("custom-key", fields[0].Name);
        Assert.Equal("custom-header", fields[0].Value);
    }

    [Fact]
    public void Decodes_a_huffman_coded_value()
    {
        // ":authority: www.example.com" with the value Huffman-coded.
        var fields = Decode("418cf1e3c2e5f23a6ba0ab90f4ff");

        Assert.Equal(":authority", fields[0].Name);
        Assert.Equal("www.example.com", fields[0].Value);
    }

    [Fact]
    public void Remembers_indexed_entries_across_blocks()
    {
        // The dynamic table is connection state: block two's index only means anything because of
        // what block one put there.
        var decoder = new HpackDecoder();

        Decode("400a637573746f6d2d6b65790d637573746f6d2d686561646572", decoder);
        var second = Decode("be", decoder);   // index 62 = the first dynamic entry

        Assert.Equal("custom-key", second[0].Name);
        Assert.Equal("custom-header", second[0].Value);
    }

    [Fact]
    public void Rejects_an_index_past_the_end_of_the_table()
        => Assert.Throws<HpackException>(() => Decode("ff00"));

    [Fact]
    public void Rejects_index_zero()
        => Assert.Throws<HpackException>(() => Decode("80"));

    [Fact]
    public void Rejects_a_table_size_update_above_the_agreed_maximum()
        => Assert.Throws<HpackException>(() => Decode("3fe1ff03", new HpackDecoder(4096) { MaxAllowedTableSize = 4096 }));

    [Fact]
    public void Round_trips_through_the_encoder()
    {
        var encoder = new HpackEncoder();
        var block = new ArrayBufferWriter<byte>();

        List<HeaderField> original =
        [
            new(":status", "200"),
            new("content-type", "application/json"),
            new("x-custom", "value")
        ];

        encoder.Encode(original, block);

        var decoded = new List<HeaderField>();
        new HpackDecoder().Decode(block.WrittenSpan, decoded);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Uses_the_static_table_for_well_known_fields()
    {
        var encoder = new HpackEncoder();
        var block = new ArrayBufferWriter<byte>();

        encoder.Encode(new HeaderField(":status", "200"), block);

        // One byte: an exact static-table hit is the whole point of the table.
        Assert.Equal(1, block.WrittenCount);
        Assert.Equal(0x88, block.WrittenSpan[0]);
    }
}

/// <summary>
/// End-to-end against <see cref="HttpClient"/> speaking real HTTP/2 over cleartext (prior
/// knowledge). This is the test that matters: it exercises the preface, SETTINGS, HPACK in both
/// directions, flow control and framing against an implementation that had no say in any of them.
/// </summary>
public class Http2InteropTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static HttpClient CreateClient(int port) => new(new SocketsHttpHandler())
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        DefaultRequestVersion = HttpVersion.Version20,

        // Exact, so a silent fallback to HTTP/1.1 fails the test rather than passing it.
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        Timeout = TimeSpan.FromSeconds(30)
    };

    [Fact]
    public async Task Serves_a_request_over_http2()
    {
        await using var server = await TestServer.StartAsync(
            app => app.OnGet("/ping", ctx => ctx.Response.WriteAsync($"pong via {ctx.Request.Protocol}"))
        );

        using var client = CreateClient(server.Port);
        var response = await client.GetAsync("/ping", Token);

        Assert.Equal(HttpVersion.Version20, response.Version);
        Assert.Equal("pong via HTTP/2", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Carries_headers_both_ways()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/echo", ctx =>
        {
            ctx.Response.Headers["X-Server-Header"] = "from-server";
            return ctx.Response.WriteAsync(ctx.Request.Headers.GetFirst("X-Client-Header") ?? "(none)");
        }));

        using var client = CreateClient(server.Port);

        var request = new HttpRequestMessage(HttpMethod.Get, "/echo");
        request.Headers.Add("X-Client-Header", "from-client");

        var response = await client.SendAsync(request, Token);

        Assert.Equal("from-client", await response.Content.ReadAsStringAsync(Token));
        Assert.Equal("from-server", response.Headers.GetValues("X-Server-Header").Single());
    }

    [Fact]
    public async Task Routes_and_binds_exactly_as_over_http1()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.OnGet("/users/{id:int}", ctx => ctx.Response.WriteAsync($"user {ctx.Request.RouteValues["id"]}"));
            app.OnGet("/search", ctx => ctx.Response.WriteAsync($"q={ctx.Request.Query.GetFirst("q")}"));
        });

        using var client = CreateClient(server.Port);

        Assert.Equal("user 42", await client.GetStringAsync("/users/42", Token));
        Assert.Equal("q=shiny", await client.GetStringAsync("/search?q=shiny", Token));
    }

    [Fact]
    public async Task Reads_a_request_body()
    {
        await using var server = await TestServer.StartAsync(app => app.OnPost("/echo", async ctx =>
        {
            var body = await ctx.Request.ReadBodyAsStringAsync(cancellationToken: ctx.RequestAborted);
            await ctx.Response.WriteAsync($"echo:{body}");
        }));

        using var client = CreateClient(server.Port);
        var response = await client.PostAsync("/echo", new StringContent("payload"), Token);

        Assert.Equal("echo:payload", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Carries_a_body_larger_than_the_initial_flow_control_window()
    {
        // Comfortably past 65535, so this only passes if WINDOW_UPDATE is being sent and honoured
        // in both directions.
        var payload = new string('x', 512 * 1024);

        await using var server = await TestServer.StartAsync(app => app.OnPost("/big", async ctx =>
        {
            var body = await ctx.Request.ReadBodyAsStringAsync(
                maxLength: 4 * 1024 * 1024,
                cancellationToken: ctx.RequestAborted
            );

            await ctx.Response.WriteAsync(body);
        }));

        using var client = CreateClient(server.Port);
        var response = await client.PostAsync("/big", new StringContent(payload), Token);
        var echoed = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(payload.Length, echoed.Length);
        Assert.Equal(payload, echoed);
    }

    [Fact]
    public async Task Multiplexes_concurrent_requests_on_one_connection()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/slow/{id}", async ctx =>
        {
            await Task.Delay(50, ctx.RequestAborted);
            await ctx.Response.WriteAsync(ctx.Request.RouteValues["id"]!);
        }));

        using var client = CreateClient(server.Port);

        // One connection, twenty streams in flight. Serialising them would take a second; the
        // point of HTTP/2 is that it does not.
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(i => client.GetStringAsync($"/slow/{i}", Token))
        );

        Assert.Equal(Enumerable.Range(0, 20).Select(i => i.ToString()), responses);
    }

    [Fact]
    public async Task Returns_status_codes_and_json()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.OnGet("/missing", _ => Results.NotFound());
            app.OnGet("/json", _ => Results.Ok(new Thing(3, "over-h2"), TestJson.Default.Thing));
        });

        using var client = CreateClient(server.Port);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/missing", Token)).StatusCode);

        var json = await client.GetAsync("/json", Token);
        Assert.Equal("""{"id":3,"name":"over-h2"}""", await json.Content.ReadAsStringAsync(Token));
        Assert.Equal("application/json", json.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Reuses_one_connection_across_requests()
    {
        await using var server = await TestServer.StartAsync(
            app => app.OnGet("/id", ctx => ctx.Response.WriteAsync(ctx.Connection.ConnectionId))
        );

        using var client = CreateClient(server.Port);

        var first = await client.GetStringAsync("/id", Token);
        var second = await client.GetStringAsync("/id", Token);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Still_serves_http1_clients_on_the_same_port()
    {
        await using var server = await TestServer.StartAsync(
            app => app.OnGet("/ping", ctx => ctx.Response.WriteAsync(ctx.Request.Protocol))
        );

        // The default client speaks HTTP/1.1; protocol selection is per connection, not per server.
        Assert.Equal("HTTP/1.1", await server.Client.GetStringAsync("/ping", Token));

        using var http2 = CreateClient(server.Port);
        Assert.Equal("HTTP/2", await http2.GetStringAsync("/ping", Token));
    }

    [Fact]
    public async Task Falls_back_to_http1_when_http2_is_turned_off()
    {
        await using var server = await TestServer.StartAsync(
            app => app.OnGet("/ping", ctx => ctx.Response.WriteAsync(ctx.Request.Protocol)),
            builder => builder.Options.Http2.Enabled = false
        );

        Assert.Equal("HTTP/1.1", await server.Client.GetStringAsync("/ping", Token));
    }

    [Fact]
    public async Task Streams_a_response_written_in_pieces()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/stream", async ctx =>
        {
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.StartAsync(ctx.RequestAborted);

            for (var i = 0; i < 5; i++)
            {
                await ctx.Response.BodyWriter.WriteAsync(Encoding.ASCII.GetBytes($"chunk{i};"), ctx.RequestAborted);
                await ctx.Response.BodyWriter.FlushAsync(ctx.RequestAborted);
            }
        }));

        using var client = CreateClient(server.Port);
        var response = await client.GetAsync("/stream", Token);

        Assert.Equal("chunk0;chunk1;chunk2;chunk3;chunk4;", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Reports_the_client_address_and_authority()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/who", ctx =>
            ctx.Response.WriteAsync($"{ctx.Request.Host}|{ctx.Connection.RemoteIpAddress}")));

        using var client = CreateClient(server.Port);
        var text = await client.GetStringAsync("/who", Token);

        Assert.StartsWith($"127.0.0.1:{server.Port}|127.0.0.1", text);
    }
}

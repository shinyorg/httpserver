using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Endpoints;
using Shiny.Net.HttpServer.Negotiation;

namespace Shiny.Net.HttpServer.Tests;

// ---------------------------------------------------------------------------
// Formats other than JSON: XML, MessagePack, and a binary format whose codec
// the app supplies (protobuf). Everything here goes over a real socket, and the
// byte-level assertions are deliberate — a formatter that produces something no
// other implementation can read is the failure mode worth catching, and it looks
// exactly like success from the inside.
// ---------------------------------------------------------------------------

public enum ProbeKind
{
    Thermal,
    Humidity
}

/// <summary>
/// <c>PostalCode</c> is the interesting member: a string whose value looks like a number, which is
/// what tells a type-directed reader apart from one guessing at text.
/// </summary>
public record Probe(string Name, string PostalCode, double Value, bool Active, ProbeKind Kind, string? Note);

public record ProbeSite(string Site, List<Probe> Probes, Dictionary<string, int> Counts);

public record Point(int X, int Y);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Probe))]
[JsonSerializable(typeof(ProbeSite))]
[JsonSerializable(typeof(Point))]
[JsonSerializable(typeof(List<Probe>))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(ProbeKind))]
public partial class FormatsJson : JsonSerializerContext;

[Route("/api/probes")]
public class ProbeEndpoints
{
    /// <summary>Echoes the body back, so one request exercises reading and writing in one hop.</summary>
    [Post]
    public IActionResult Create(Probe probe) => new OkObjectResult(probe);

    [Post("/point")]
    public IActionResult CreatePoint(Point point) => new OkObjectResult(point);
}

public class MessagePackCodecTests
{
    static byte[] Encode(string json) => MessagePackCodec.FromJson(Encoding.UTF8.GetBytes(json));

    static string Decode(byte[] messagePack) => Encoding.UTF8.GetString(MessagePackCodec.ToJson(messagePack));

    /// <summary>
    /// The integer families, at every boundary between them. Off-by-one here produces bytes that
    /// decode to a different number in every other MessagePack implementation, and to the right one
    /// in this test if the same mistake is made twice — hence exact bytes rather than a round trip.
    /// </summary>
    [Theory]
    [InlineData("0", "00")]
    [InlineData("1", "01")]
    [InlineData("127", "7f")]
    [InlineData("128", "cc80")]
    [InlineData("255", "ccff")]
    [InlineData("256", "cd0100")]
    [InlineData("65535", "cdffff")]
    [InlineData("65536", "ce00010000")]
    [InlineData("4294967295", "ceffffffff")]
    [InlineData("4294967296", "cf0000000100000000")]
    [InlineData("-1", "ff")]
    [InlineData("-32", "e0")]
    [InlineData("-33", "d0df")]
    [InlineData("-128", "d080")]
    [InlineData("-129", "d1ff7f")]
    [InlineData("-32768", "d18000")]
    [InlineData("-32769", "d2ffff7fff")]
    [InlineData("-2147483648", "d280000000")]
    [InlineData("-2147483649", "d3ffffffff7fffffff")]
    public void Encodes_integers_in_the_tightest_family(string json, string expected)
        => Assert.Equal(expected, Convert.ToHexString(Encode(json)).ToLowerInvariant());

    [Theory]
    [InlineData("null", "c0")]
    [InlineData("true", "c3")]
    [InlineData("false", "c2")]
    [InlineData("1.5", "cb3ff8000000000000")]
    [InlineData("\"\"", "a0")]
    [InlineData("\"hi\"", "a26869")]
    [InlineData("[]", "90")]
    [InlineData("{}", "80")]
    [InlineData("[1,2]", "920102")]
    [InlineData("{\"a\":1}", "81a16101")]
    public void Encodes_the_other_types(string json, string expected)
        => Assert.Equal(expected, Convert.ToHexString(Encode(json)).ToLowerInvariant());

    /// <summary>31 bytes is the last fixstr; 32 is the first str8.</summary>
    [Fact]
    public void Switches_string_families_at_the_fixstr_boundary()
    {
        Assert.Equal(0xbf, Encode($"\"{new string('x', 31)}\"")[0]);
        Assert.Equal(0xd9, Encode($"\"{new string('x', 32)}\"")[0]);
    }

    [Fact]
    public void Switches_container_families_at_the_fixarray_boundary()
    {
        Assert.Equal(0x9f, Encode("[" + string.Join(',', Enumerable.Repeat("1", 15)) + "]")[0]);
        Assert.Equal(0xdc, Encode("[" + string.Join(',', Enumerable.Repeat("1", 16)) + "]")[0]);
    }

    [Theory]
    [InlineData("{\"a\":1,\"b\":[true,null,\"x\"],\"c\":{\"d\":-7}}")]
    [InlineData("[[[1]]]")]
    [InlineData("{\"unicode\":\"\\u00e9\\u4e2d\"}")]
    [InlineData("[0,127,128,-1,-33,65536,1.25,-2147483649]")]
    public void Round_trips_a_document(string json)
        => Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(Decode(Encode(json)))),
            $"{json} came back as {Decode(Encode(json))}"
        );

    /// <summary>Bytes built by hand the way any other implementation would build them.</summary>
    [Fact]
    public void Reads_bytes_it_did_not_write()
    {
        // fixmap(1) "n" fixstr(3) "abc"
        byte[] wire = [0x81, 0xa1, (byte)'n', 0xa3, (byte)'a', (byte)'b', (byte)'c'];

        Assert.Equal("{\"n\":\"abc\"}", Decode(wire));
    }

    /// <summary>float32 is never written and must still be read — plenty of encoders emit it.</summary>
    [Fact]
    public void Reads_a_float32()
        => Assert.Equal("[1.5]", Decode([0x91, 0xca, 0x3f, 0xc0, 0x00, 0x00]));

    /// <summary>MessagePack has a binary type and JSON does not, so it arrives the way JSON carries bytes.</summary>
    [Fact]
    public void Reads_binary_as_base64()
        => Assert.Equal("[\"AQID\"]", Decode([0x91, 0xc4, 0x03, 0x01, 0x02, 0x03]));

    [Fact]
    public void Refuses_a_truncated_body()
        => Assert.Throws<MessagePackFormatException>(() => Decode([0x81, 0xa1, (byte)'n']));

    /// <summary>
    /// A declared count the body cannot satisfy is refused before anything is allocated for it.
    /// </summary>
    [Fact]
    public void Refuses_a_container_larger_than_the_body()
        => Assert.Throws<MessagePackFormatException>(() => Decode([0xdd, 0xff, 0xff, 0xff, 0xff]));

    [Fact]
    public void Refuses_trailing_bytes()
        => Assert.Throws<MessagePackFormatException>(() => Decode([0x01, 0x02]));

    /// <summary>
    /// Integer keys carry no member names, so binding them would be guessing. Refusing is a 400 the
    /// caller can act on; emitting "0" as a property name is a DTO full of defaults that looks fine.
    /// </summary>
    [Fact]
    public void Refuses_integer_map_keys()
        => Assert.Throws<MessagePackFormatException>(() => Decode([0x81, 0x00, 0x01]));

    [Fact]
    public void Refuses_an_extension_type()
        => Assert.Throws<MessagePackFormatException>(() => Decode([0xd4, 0x00, 0x00]));
}

public class SerializationFormatTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static readonly Probe Sample = new("kitchen", "01234", 21.5, true, ProbeKind.Humidity, null);

    public SerializationFormatTests() => JsonTypeInfoRegistry.Register(FormatsJson.Default);

    static Task<TestServer> StartAsync(Action<ContentNegotiationOptions>? configure = null) => TestServer.StartAsync(
        app =>
        {
            app.MapProbeEndpoints();
            app.MapGet("/probe", _ => Results.Ok(Sample));
            app.MapGet("/probe/json", _ => Results.Json(Sample, StatusCodes.Status200OK));
            app.MapGet("/probe/negotiated", _ => Results.Negotiate(Sample));
            app.MapGet("/point", _ => Results.Negotiate(new Point(3, 300)));
        },
        builder => builder.AddContentNegotiation(configure)
    );

    static async Task<HttpResponseMessage> GetAsync(TestServer server, string path, string accept)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Accept", accept);

        return await server.Client.SendAsync(request, Token);
    }

    static async Task<HttpResponseMessage> PostAsync(
        TestServer server,
        string path,
        byte[] body,
        string? contentType
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(body) };

        request.Content.Headers.ContentType = contentType is null ? null : new MediaTypeHeaderValue(contentType);

        return await server.Client.SendAsync(request, Token);
    }

    // ---- Reading bodies ----

    /// <summary>
    /// The claim that makes this worth having: an existing generated endpoint accepts XML because the
    /// app registered a formatter, not because the endpoint was changed.
    /// </summary>
    [Fact]
    public async Task Reads_an_xml_body_on_a_generated_endpoint()
    {
        await using var server = await StartAsync(o => o.AddXml());

        var xml = """
            <Probe>
                <name>kitchen</name>
                <postalCode>01234</postalCode>
                <value>21.5</value>
                <active>true</active>
                <kind>1</kind>
            </Probe>
            """;

        var response = await PostAsync(server, "/api/probes", Encoding.UTF8.GetBytes(xml), "application/xml");

        response.EnsureSuccessStatusCode();
        var echoed = await response.Content.ReadFromJsonAsync<Probe>(FormatsJson.Default.Options, Token);

        Assert.Equal(Sample, echoed);
    }

    /// <summary>
    /// A leading zero is the whole reason the reader is type-directed. Inferring from the text would
    /// make this postal code the number 1234.
    /// </summary>
    [Fact]
    public async Task Keeps_a_numeric_looking_string_a_string()
    {
        await using var server = await StartAsync(o => o.AddXml());

        var xml = "<Probe><name>x</name><postalCode>01234</postalCode><value>1</value>"
            + "<active>false</active><kind>0</kind></Probe>";

        var response = await PostAsync(server, "/api/probes", Encoding.UTF8.GetBytes(xml), "application/xml");

        response.EnsureSuccessStatusCode();
        Assert.Contains("\"01234\"", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reads_a_messagepack_body_on_a_generated_endpoint()
    {
        await using var server = await StartAsync(o => o.AddMessagePack());

        // fixmap(2) "x" 3, "y" 300 — built by hand, so this proves the wire format rather than
        // this codec's agreement with itself.
        byte[] wire = [0x82, 0xa1, (byte)'x', 0x03, 0xa1, (byte)'y', 0xcd, 0x01, 0x2c];

        var response = await PostAsync(server, "/api/probes/point", wire, "application/msgpack");

        response.EnsureSuccessStatusCode();
        Assert.Equal(new Point(3, 300), await response.Content.ReadFromJsonAsync<Point>(FormatsJson.Default.Options, Token));
    }

    [Fact]
    public async Task Still_reads_json_when_other_formats_are_registered()
    {
        await using var server = await StartAsync(o => o.AddXml().AddMessagePack());

        var json = JsonSerializer.SerializeToUtf8Bytes(Sample, FormatsJson.Default.Probe);
        var response = await PostAsync(server, "/api/probes", json, "application/json");

        response.EnsureSuccessStatusCode();
        Assert.Equal(Sample, await response.Content.ReadFromJsonAsync<Probe>(FormatsJson.Default.Options, Token));
    }

    /// <summary>
    /// Plenty of clients omit the header, and this server has always read those bodies as JSON.
    /// Answering 415 to them now would break working callers to make a point about a header.
    /// </summary>
    [Fact]
    public async Task Treats_a_body_with_no_content_type_as_json()
    {
        await using var server = await StartAsync(o => o.AddXml());

        var json = JsonSerializer.SerializeToUtf8Bytes(Sample, FormatsJson.Default.Probe);
        var response = await PostAsync(server, "/api/probes", json, contentType: null);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// The difference between "your JSON is broken" and "I do not speak protobuf". A 400 sends the
    /// caller hunting for a syntax error that is not there.
    /// </summary>
    [Fact]
    public async Task Answers_415_for_a_content_type_nothing_reads()
    {
        await using var server = await StartAsync();

        var response = await PostAsync(server, "/api/probes", [1, 2, 3], "application/x-protobuf");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);

        // The list of what would have worked is the actionable part of a 415.
        Assert.Contains("application/json", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Answers_400_for_a_body_in_the_right_format_that_will_not_parse()
    {
        await using var server = await StartAsync(o => o.AddXml());

        var response = await PostAsync(
            server,
            "/api/probes",
            Encoding.UTF8.GetBytes("<Probe><name>unclosed"),
            "application/xml"
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Refuses_a_body_over_the_formatter_limit()
    {
        await using var server = await StartAsync(o =>
        {
            o.AddXml();
            ((XmlInputFormatter)o.InputFormatters[^1]).MaxBodyBytes = 64;
        });

        var padded = $"<Probe><name>{new string('x', 500)}</name></Probe>";
        var response = await PostAsync(server, "/api/probes", Encoding.UTF8.GetBytes(padded), "application/xml");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    /// <summary>An XML endpoint is the classic place to get talked into reading a local file.</summary>
    [Fact]
    public async Task Does_not_expand_external_entities()
    {
        await using var server = await StartAsync(o => o.AddXml());

        var attack = """
            <!DOCTYPE probe [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <Probe><name>&xxe;</name></Probe>
            """;

        var response = await PostAsync(server, "/api/probes", Encoding.UTF8.GetBytes(attack), "application/xml");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Writing bodies ----

    [Fact]
    public async Task Writes_xml_when_it_is_asked_for()
    {
        await using var server = await StartAsync(o => o.AddXml());

        var response = await GetAsync(server, "/probe/negotiated", "application/xml");

        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.Contains("<name>kitchen</name>", body, StringComparison.Ordinal);
        Assert.Contains("<postalCode>01234</postalCode>", body, StringComparison.Ordinal);

        // Null is an element that says so, not an absent one — the reader has to be able to tell the
        // difference between "no value" and "member not sent".
        Assert.Contains("nil=\"true\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writes_xml_under_the_text_spelling_too()
    {
        await using var server = await StartAsync(o => o.AddXml());

        var response = await GetAsync(server, "/probe/negotiated", "text/xml");

        Assert.Equal("text/xml", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Writes_messagepack_when_it_is_asked_for()
    {
        await using var server = await StartAsync(o => o.AddMessagePack());

        var response = await GetAsync(server, "/point", "application/msgpack");

        Assert.Equal("application/msgpack", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            new byte[] { 0x82, 0xa1, (byte)'x', 0x03, 0xa1, (byte)'y', 0xcd, 0x01, 0x2c },
            await response.Content.ReadAsByteArrayAsync(Token)
        );
    }

    [Fact]
    public async Task Writes_messagepack_under_the_legacy_spelling_too()
    {
        await using var server = await StartAsync(o => o.AddMessagePack());

        var response = await GetAsync(server, "/point", "application/x-msgpack");

        Assert.Equal("application/x-msgpack", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>Every value round-trips through the wire and back into the same object.</summary>
    [Theory]
    [InlineData("application/xml")]
    [InlineData("application/msgpack")]
    public async Task Round_trips_a_value_through_the_wire(string mediaType)
    {
        await using var server = await StartAsync(o => o.AddXml().AddMessagePack());

        var out_ = await GetAsync(server, "/probe/negotiated", mediaType);
        var body = await out_.Content.ReadAsByteArrayAsync(Token);

        var back = await PostAsync(server, "/api/probes", body, mediaType);

        back.EnsureSuccessStatusCode();
        Assert.Equal(Sample, await back.Content.ReadFromJsonAsync<Probe>(FormatsJson.Default.Options, Token));
    }

    /// <summary>Collections and dictionaries, which have their own shape in both formats.</summary>
    [Fact]
    public async Task Round_trips_collections_and_dictionaries()
    {
        var site = new ProbeSite("north", [Sample, Sample with { Name = "hall" }], new() { ["ok"] = 2 });

        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.MapGet("/site", _ => Results.Negotiate(site));
                app.MapPost("/site", async ctx =>
                {
                    var read = await EndpointBinder.TryReadBodyAsync<ProbeSite>(ctx);
                    return read.Success ? Results.Ok(read.Value!) : Results.StatusCode((int)read.Status + 900);
                });
            },
            builder => builder.AddContentNegotiation(o => o.AddXml().AddMessagePack())
        );

        foreach (var mediaType in new[] { "application/xml", "application/msgpack" })
        {
            var written = await GetAsync(server, "/site", mediaType);
            var body = await written.Content.ReadAsByteArrayAsync(Token);

            var back = await PostAsync(server, "/site", body, mediaType);

            back.EnsureSuccessStatusCode();
            var echoed = await back.Content.ReadFromJsonAsync<ProbeSite>(FormatsJson.Default.Options, Token);

            Assert.Equal(site.Site, echoed!.Site);
            Assert.Equal(site.Probes, echoed.Probes);
            Assert.Equal(site.Counts, echoed.Counts);
        }
    }

    // ---- Negotiating by default ----

    /// <summary>
    /// Off by default: an app whose contract says JSON must not start answering XML because a client
    /// sent a header.
    /// </summary>
    [Fact]
    public async Task Does_not_negotiate_results_by_default()
    {
        await using var server = await StartAsync(o => o.AddXml());

        var response = await GetAsync(server, "/probe", "application/xml");

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Negotiates_results_when_asked_to()
    {
        await using var server = await StartAsync(o =>
        {
            o.NegotiateByDefault = true;
            o.AddXml();
        });

        Assert.Equal(
            "application/xml",
            (await GetAsync(server, "/probe", "application/xml")).Content.Headers.ContentType?.MediaType
        );

        // A client with no preference still gets the server's, which is JSON.
        Assert.Equal(
            "application/json",
            (await GetAsync(server, "/probe", "*/*")).Content.Headers.ContentType?.MediaType
        );
    }

    /// <summary>The two spellings of the same intent must not disagree about what they send.</summary>
    [Fact]
    public async Task Negotiates_the_action_result_spelling_on_the_same_terms()
    {
        await using var server = await StartAsync(o =>
        {
            o.NegotiateByDefault = true;
            o.AddXml();
        });

        var json = JsonSerializer.SerializeToUtf8Bytes(Sample, FormatsJson.Default.Probe);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/probes")
        {
            Content = new ByteArrayContent(json)
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("Accept", "application/xml");

        var response = await server.Client.SendAsync(request, Token);

        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary><c>Results.Json</c> names a format, so it is not up for negotiation.</summary>
    [Fact]
    public async Task Leaves_an_explicitly_json_result_alone()
    {
        await using var server = await StartAsync(o =>
        {
            o.NegotiateByDefault = true;
            o.AddXml();
        });

        var response = await GetAsync(server, "/probe/json", "application/xml");

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Answers_406_for_a_format_that_was_never_registered()
    {
        await using var server = await StartAsync();

        Assert.Equal(
            HttpStatusCode.NotAcceptable,
            (await GetAsync(server, "/probe/negotiated", "application/xml")).StatusCode
        );
    }
}

/// <summary>
/// A binary format whose codec the app supplies. The encoding below is real protobuf — two varint
/// fields — because a formatter that carries bytes no protobuf client can read would look identical
/// to a working one in a test that only round-trips against itself.
/// </summary>
public class SuppliedCodecFormatTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    public SuppliedCodecFormatTests() => JsonTypeInfoRegistry.Register(FormatsJson.Default);

    /// <summary>Field 1 and field 2 as protobuf varints — <c>08 &lt;x&gt; 10 &lt;y&gt;</c>.</summary>
    static byte[] EncodePoint(Point point)
    {
        var buffer = new List<byte> { 0x08 };
        WriteVarint(buffer, point.X);
        buffer.Add(0x10);
        WriteVarint(buffer, point.Y);

        return [.. buffer];

        static void WriteVarint(List<byte> into, int value)
        {
            var remaining = (uint)value;
            while (remaining >= 0x80)
            {
                into.Add((byte)(remaining | 0x80));
                remaining >>= 7;
            }

            into.Add((byte)remaining);
        }
    }

    static Point DecodePoint(byte[] bytes)
    {
        var position = 0;
        int x = 0, y = 0;

        while (position < bytes.Length)
        {
            var tag = bytes[position++];
            var value = ReadVarint(bytes, ref position);

            switch (tag)
            {
                case 0x08:
                    x = value;
                    break;

                case 0x10:
                    y = value;
                    break;

                default:
                    throw new InvalidDataException($"Unexpected field tag 0x{tag:x2}.");
            }
        }

        return new Point(x, y);

        static int ReadVarint(byte[] from, ref int at)
        {
            var result = 0;
            var shift = 0;

            while (true)
            {
                if (at >= from.Length)
                    throw new InvalidDataException("Varint ran off the end of the message.");

                var b = from[at++];
                result |= (b & 0x7f) << shift;

                if ((b & 0x80) == 0)
                    return result;

                shift += 7;
            }
        }
    }

    static Task<TestServer> StartAsync() => TestServer.StartAsync(
        app =>
        {
            app.MapGet("/point", _ => Results.Negotiate(new Point(3, 300)));
            app.MapGet("/probe", _ => Results.Negotiate(new Probe("k", "1", 1, true, ProbeKind.Thermal, null)));
            app.MapPost("/point", async ctx =>
            {
                var read = await EndpointBinder.TryReadBodyAsync<Point>(ctx);

                return read.Success
                    ? Results.Ok(read.Value!)
                    : Results.StatusCode(StatusCodes.Status400BadRequest);
            });
        },
        builder => builder.AddContentNegotiation(
            o => o.AddProtobuf(p => p.Add<Point>(EncodePoint, DecodePoint))
        )
    );

    static async Task<HttpResponseMessage> GetAsync(TestServer server, string accept)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/point");
        request.Headers.TryAddWithoutValidation("Accept", accept);

        return await server.Client.SendAsync(request, Token);
    }

    [Fact]
    public async Task Writes_the_supplied_encoding_verbatim()
    {
        await using var server = await StartAsync();

        var response = await GetAsync(server, "application/x-protobuf");

        Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            new byte[] { 0x08, 0x03, 0x10, 0xac, 0x02 },
            await response.Content.ReadAsByteArrayAsync(Token)
        );
    }

    [Fact]
    public async Task Reads_the_supplied_encoding()
    {
        await using var server = await StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/point")
        {
            Content = new ByteArrayContent([0x08, 0x03, 0x10, 0xac, 0x02])
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

        var response = await server.Client.SendAsync(request, Token);

        response.EnsureSuccessStatusCode();
        Assert.Equal(new Point(3, 300), await response.Content.ReadFromJsonAsync<Point>(FormatsJson.Default.Options, Token));
    }

    /// <summary>The other spellings are read but never written — a response has to pick one.</summary>
    [Theory]
    [InlineData("application/protobuf")]
    [InlineData("application/vnd.google.protobuf")]
    public async Task Reads_the_alternate_spellings(string mediaType)
    {
        await using var server = await StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/point")
        {
            Content = new ByteArrayContent([0x08, 0x07, 0x10, 0x09])
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);

        var response = await server.Client.SendAsync(request, Token);

        response.EnsureSuccessStatusCode();
        Assert.Equal(new Point(7, 9), await response.Content.ReadFromJsonAsync<Point>(FormatsJson.Default.Options, Token));
    }

    /// <summary>
    /// A codec that throws is describing a bad request, whatever exception type it picked. Letting it
    /// escape would report the client's mistake as a 500.
    /// </summary>
    [Fact]
    public async Task Turns_a_codec_failure_into_a_bad_request()
    {
        await using var server = await StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/point")
        {
            Content = new ByteArrayContent([0x2a, 0x01])
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

        var response = await server.Client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// A type with no codec has no protobuf representation, so it negotiates away rather than
    /// failing at serialization time.
    /// </summary>
    [Fact]
    public async Task Declines_a_type_with_no_codec()
    {
        await using var server = await StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
        request.Headers.TryAddWithoutValidation("Accept", "application/x-protobuf");

        var response = await server.Client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
    }
}

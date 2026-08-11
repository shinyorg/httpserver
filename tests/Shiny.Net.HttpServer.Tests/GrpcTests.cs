using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Grpc.Core;
using Grpc.Net.Client;
using Shiny.Net.HttpServer.Grpc;
using Shiny.Net.HttpServer.Grpc.Internal;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The gRPC endpoints, driven by Google's own client over real HTTP/2.
/// <para>
/// Messages are plain UTF-8 strings rather than protobuf, on purpose. Nothing in the server knows
/// what a message is — marshalling is the caller's — so a protobuf dependency here would test
/// Google.Protobuf, not the framing, the trailers or the status mapping, which is what can actually
/// be wrong.
/// </para>
/// </summary>
public class GrpcInteropTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    const string ServiceName = "test.Echo";

    static readonly Marshaller<string> ClientMarshaller = Marshallers.Create(
        Encoding.UTF8.GetBytes,
        bytes => Encoding.UTF8.GetString(bytes)
    );

    static Method<string, string> ClientMethod(string name, MethodType type)
        => new(type, ServiceName, name, ClientMarshaller, ClientMarshaller);

    static GrpcChannel CreateChannel(int port) => GrpcChannel.ForAddress(
        $"http://127.0.0.1:{port}",
        new GrpcChannelOptions { HttpHandler = new SocketsHttpHandler() }
    );

    static Task<TestServer> StartAsync(Action<GrpcServiceBuilder> configure, Action<GrpcOptions>? options = null)
        => TestServer.StartAsync(app => app.MapGrpcService(ServiceName, svc =>
        {
            svc.AddMarshaller<string>(Encoding.UTF8.GetBytes, bytes => Encoding.UTF8.GetString(bytes));
            options?.Invoke(svc.Options);
            configure(svc);
        }));

    [Fact]
    public async Task Answers_a_unary_call()
    {
        await using var server = await StartAsync(svc => svc.MapUnary<string, string>(
            "Say",
            (request, _) => new ValueTask<string>($"hello {request}")
        ));

        using var channel = CreateChannel(server.Port);

        var reply = await channel.CreateCallInvoker().AsyncUnaryCall(
            ClientMethod("Say", MethodType.Unary),
            null,
            new CallOptions(cancellationToken: Token),
            "world"
        );

        Assert.Equal("hello world", reply);
    }

    [Fact]
    public async Task Reports_a_thrown_status_to_the_caller()
    {
        await using var server = await StartAsync(svc => svc.MapUnary<string, string>(
            "Say",
            (_, _) => throw new GrpcStatusException(GrpcStatusCode.NotFound, "no such greeting")
        ));

        using var channel = CreateChannel(server.Port);

        var error = await Assert.ThrowsAsync<RpcException>(async () => await channel.CreateCallInvoker().AsyncUnaryCall(
            ClientMethod("Say", MethodType.Unary),
            null,
            new CallOptions(cancellationToken: Token),
            "world"
        ));

        Assert.Equal(StatusCode.NotFound, error.StatusCode);
        Assert.Equal("no such greeting", error.Status.Detail);
    }

    [Fact]
    public async Task Hides_the_detail_of_an_unhandled_exception()
    {
        await using var server = await StartAsync(svc => svc.MapUnary<string, string>(
            "Say",
            (_, _) => throw new InvalidOperationException("connection string is Server=secret;")
        ));

        using var channel = CreateChannel(server.Port);

        var error = await Assert.ThrowsAsync<RpcException>(async () => await channel.CreateCallInvoker().AsyncUnaryCall(
            ClientMethod("Say", MethodType.Unary),
            null,
            new CallOptions(cancellationToken: Token),
            "world"
        ));

        Assert.Equal(StatusCode.Unknown, error.StatusCode);
        Assert.DoesNotContain("secret", error.Status.Detail);
    }

    [Fact]
    public async Task Streams_responses()
    {
        await using var server = await StartAsync(svc => svc.MapServerStreaming<string, string>("Repeat", Repeat));

        using var channel = CreateChannel(server.Port);

        using var call = channel.CreateCallInvoker().AsyncServerStreamingCall(
            ClientMethod("Repeat", MethodType.ServerStreaming),
            null,
            new CallOptions(cancellationToken: Token),
            "tick"
        );

        var received = new List<string>();
        while (await call.ResponseStream.MoveNext(Token))
            received.Add(call.ResponseStream.Current);

        Assert.Equal(["tick 1", "tick 2", "tick 3"], received);

        static async IAsyncEnumerable<string> Repeat(string request, GrpcCallContext context)
        {
            for (var i = 1; i <= 3; i++)
            {
                await Task.Yield();
                yield return $"{request} {i}";
            }
        }
    }

    [Fact]
    public async Task Reads_a_client_stream()
    {
        await using var server = await StartAsync(svc => svc.MapClientStreaming<string, string>(
            "Collect",
            async (requests, _) =>
            {
                var parts = new List<string>();
                await foreach (var request in requests)
                    parts.Add(request);

                return string.Join("+", parts);
            }
        ));

        using var channel = CreateChannel(server.Port);

        using var call = channel.CreateCallInvoker().AsyncClientStreamingCall(
            ClientMethod("Collect", MethodType.ClientStreaming),
            null,
            new CallOptions(cancellationToken: Token)
        );

        await call.RequestStream.WriteAsync("a", Token);
        await call.RequestStream.WriteAsync("b", Token);
        await call.RequestStream.WriteAsync("c", Token);
        await call.RequestStream.CompleteAsync();

        Assert.Equal("a+b+c", await call.ResponseAsync);
    }

    [Fact]
    public async Task Streams_both_ways_at_once()
    {
        await using var server = await StartAsync(svc => svc.MapDuplexStreaming<string, string>("Echo", Echo));

        using var channel = CreateChannel(server.Port);

        using var call = channel.CreateCallInvoker().AsyncDuplexStreamingCall(
            ClientMethod("Echo", MethodType.DuplexStreaming),
            null,
            new CallOptions(cancellationToken: Token)
        );

        // Written and read one at a time: a server that only answered after the request stream
        // closed would still pass a test that sent everything up front.
        await call.RequestStream.WriteAsync("one", Token);
        Assert.True(await call.ResponseStream.MoveNext(Token));
        Assert.Equal("echo:one", call.ResponseStream.Current);

        await call.RequestStream.WriteAsync("two", Token);
        Assert.True(await call.ResponseStream.MoveNext(Token));
        Assert.Equal("echo:two", call.ResponseStream.Current);

        await call.RequestStream.CompleteAsync();
        Assert.False(await call.ResponseStream.MoveNext(Token));

        static async IAsyncEnumerable<string> Echo(IAsyncEnumerable<string> requests, GrpcCallContext context)
        {
            await foreach (var request in requests)
                yield return $"echo:{request}";
        }
    }

    [Fact]
    public async Task Carries_metadata_and_trailers()
    {
        await using var server = await StartAsync(svc => svc.MapUnary<string, string>("Say", (request, context) =>
        {
            var caller = context.RequestHeaders.GetFirst("x-caller") ?? "(none)";
            context.ResponseTrailers.Set("x-handled-by", "shiny");

            return new ValueTask<string>($"{request} from {caller}");
        }));

        using var channel = CreateChannel(server.Port);

        using var call = channel.CreateCallInvoker().AsyncUnaryCall(
            ClientMethod("Say", MethodType.Unary),
            null,
            new CallOptions(new Metadata { { "x-caller", "tests" } }, cancellationToken: Token),
            "hello"
        );

        Assert.Equal("hello from tests", await call.ResponseAsync);
        Assert.Equal("shiny", call.GetTrailers().GetValue("x-handled-by"));
    }

    [Fact]
    public async Task Honours_a_deadline()
    {
        await using var server = await StartAsync(svc => svc.MapUnary<string, string>("Slow", async (_, context) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), context.CancellationToken);
            return "too late";
        }));

        using var channel = CreateChannel(server.Port);

        var error = await Assert.ThrowsAsync<RpcException>(async () => await channel.CreateCallInvoker().AsyncUnaryCall(
            ClientMethod("Slow", MethodType.Unary),
            null,
            new CallOptions(deadline: DateTime.UtcNow.AddMilliseconds(300), cancellationToken: Token),
            "hello"
        ));

        Assert.Equal(StatusCode.DeadlineExceeded, error.StatusCode);
    }

    [Fact]
    public async Task Refuses_a_message_over_the_size_limit()
    {
        await using var server = await StartAsync(
            svc => svc.MapUnary<string, string>("Say", (request, _) => new ValueTask<string>(request)),
            options => options.MaxReceiveMessageSize = 32
        );

        using var channel = CreateChannel(server.Port);

        var error = await Assert.ThrowsAsync<RpcException>(async () => await channel.CreateCallInvoker().AsyncUnaryCall(
            ClientMethod("Say", MethodType.Unary),
            null,
            new CallOptions(cancellationToken: Token),
            new string('x', 1024)
        ));

        Assert.Equal(StatusCode.ResourceExhausted, error.StatusCode);
    }

    [Fact]
    public async Task Compresses_responses_when_the_caller_accepts_it()
    {
        await using var server = await StartAsync(
            svc => svc.MapUnary<string, string>("Say", (request, _) => new ValueTask<string>(request)),
            options => options.ResponseCompression = "gzip"
        );

        using var channel = CreateChannel(server.Port);

        // Highly compressible, and large enough that gzip actually wins — the writer sends the
        // message uncompressed when compressing it would not have helped.
        var payload = new string('a', 4096);

        var reply = await channel.CreateCallInvoker().AsyncUnaryCall(
            ClientMethod("Say", MethodType.Unary),
            null,
            new CallOptions(cancellationToken: Token),
            payload
        );

        Assert.Equal(payload, reply);
    }

    [Fact]
    public async Task Answers_before_the_handler_runs_with_a_trailers_only_response()
    {
        await using var server = await StartAsync(svc => svc.MapUnary<string, string>(
            "Say",
            (request, _) => new ValueTask<string>(request)
        ));

        // An encoding the server does not implement is settled before the call begins, so the whole
        // response is one header block: no body, and the status among the headers.
        using var client = new HttpClient(new SocketsHttpHandler())
        {
            BaseAddress = new Uri($"http://127.0.0.1:{server.Port}"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var content = new ByteArrayContent(GrpcWire.Frame("hello"u8.ToArray()));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/{ServiceName}/Say")
        {
            Content = content,
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.Add("grpc-encoding", "br");

        var response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("12", response.Headers.GetValues("grpc-status").Single());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Token));
        Assert.Empty(response.TrailingHeaders);
    }

    [Fact]
    public async Task Reports_an_unknown_method_as_unimplemented()
    {
        await using var server = await StartAsync(svc => svc.MapUnary<string, string>(
            "Say",
            (request, _) => new ValueTask<string>(request)
        ));

        using var channel = CreateChannel(server.Port);

        var error = await Assert.ThrowsAsync<RpcException>(async () => await channel.CreateCallInvoker().AsyncUnaryCall(
            ClientMethod("Missing", MethodType.Unary),
            null,
            new CallOptions(cancellationToken: Token),
            "hello"
        ));

        Assert.Equal(StatusCode.Unimplemented, error.StatusCode);
    }

    [Fact]
    public async Task Rejects_native_grpc_over_http1()
    {
        await using var server = await StartAsync(svc => svc.MapUnary<string, string>(
            "Say",
            (request, _) => new ValueTask<string>(request)
        ));

        var content = new ByteArrayContent(GrpcWire.Frame("hello"u8.ToArray()));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");

        var response = await server.Client.PostAsync($"/{ServiceName}/Say", content, Token);

        Assert.Equal(HttpStatusCode.HttpVersionNotSupported, response.StatusCode);
    }
}

/// <summary>
/// gRPC-Web, which is the same call with the trailers moved into the body — and therefore the form
/// that works over HTTP/1.1, and the only form a browser can make.
/// </summary>
public class GrpcWebTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    const string ServiceName = "test.Echo";

    static Task<TestServer> StartAsync() => TestServer.StartAsync(app => app.MapGrpcService(ServiceName, svc =>
    {
        svc.AddMarshaller<string>(Encoding.UTF8.GetBytes, bytes => Encoding.UTF8.GetString(bytes));

        svc.MapUnary<string, string>("Say", (request, _) => new ValueTask<string>($"hello {request}"));

        svc.MapServerStreaming<string, string>("Repeat", Repeat);

        svc.MapUnary<string, string>(
            "Fail",
            (_, _) => throw new GrpcStatusException(GrpcStatusCode.PermissionDenied, "not for you")
        );

        svc.MapClientStreaming<string, string>("Collect", async (requests, ct) =>
        {
            var count = 0;
            await foreach (var _ in requests)
                count++;

            return count.ToString();
        });

        static async IAsyncEnumerable<string> Repeat(string request, GrpcCallContext context)
        {
            for (var i = 1; i <= 3; i++)
            {
                await Task.Yield();
                yield return $"{request} {i}";
            }
        }
    }));

    [Fact]
    public async Task Answers_a_unary_call_over_http1()
    {
        await using var server = await StartAsync();

        var response = await PostAsync(server, "Say", "application/grpc-web+proto", GrpcWire.Frame("world"u8.ToArray()));
        var body = await response.Content.ReadAsByteArrayAsync(Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpVersion.Version11, response.Version);

        var (messages, trailers) = GrpcWire.Parse(body);

        Assert.Equal("hello world", Encoding.UTF8.GetString(messages.Single()));
        Assert.Equal("0", trailers["grpc-status"]);
    }

    [Fact]
    public async Task Streams_responses_over_http1()
    {
        await using var server = await StartAsync();

        var response = await PostAsync(server, "Repeat", "application/grpc-web", GrpcWire.Frame("tick"u8.ToArray()));
        var (messages, trailers) = GrpcWire.Parse(await response.Content.ReadAsByteArrayAsync(Token));

        Assert.Equal(
            ["tick 1", "tick 2", "tick 3"],
            messages.Select(Encoding.UTF8.GetString)
        );
        Assert.Equal("0", trailers["grpc-status"]);
    }

    [Fact]
    public async Task Reports_a_failure_in_the_trailer_frame()
    {
        await using var server = await StartAsync();

        var response = await PostAsync(server, "Fail", "application/grpc-web", GrpcWire.Frame("x"u8.ToArray()));
        var (messages, trailers) = GrpcWire.Parse(await response.Content.ReadAsByteArrayAsync(Token));

        // The HTTP request succeeded; the call did not. That distinction is the whole protocol.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(messages);
        Assert.Equal("7", trailers["grpc-status"]);
        Assert.Equal("not for you", trailers["grpc-message"]);
    }

    [Fact]
    public async Task Answers_a_text_mode_call_in_base64()
    {
        await using var server = await StartAsync();

        var body = Encoding.ASCII.GetBytes(Convert.ToBase64String(GrpcWire.Frame("world"u8.ToArray())));
        var response = await PostAsync(server, "Say", "application/grpc-web-text", body);

        // The whole body is one base64 document, so it decodes in one go — no per-chunk alignment
        // for the caller to reconstruct.
        var encoded = await response.Content.ReadAsStringAsync(Token);
        var (messages, trailers) = GrpcWire.Parse(Convert.FromBase64String(encoded));

        Assert.Equal("hello world", Encoding.UTF8.GetString(messages.Single()));
        Assert.Equal("0", trailers["grpc-status"]);
    }

    [Fact]
    public async Task Refuses_a_client_streaming_method()
    {
        await using var server = await StartAsync();

        var response = await PostAsync(server, "Collect", "application/grpc-web", GrpcWire.Frame("a"u8.ToArray()));
        var (_, trailers) = GrpcWire.Parse(await response.Content.ReadAsByteArrayAsync(Token));

        Assert.Equal("12", trailers["grpc-status"]);
    }

    [Fact]
    public async Task Compresses_a_message_the_caller_said_it_could_inflate()
    {
        await using var server = await TestServer.StartAsync(app => app.MapGrpcService(ServiceName, svc =>
        {
            svc.Options.ResponseCompression = "gzip";
            svc.AddMarshaller<string>(Encoding.UTF8.GetBytes, bytes => Encoding.UTF8.GetString(bytes));
            svc.MapUnary<string, string>("Say", (request, _) => new ValueTask<string>(request));
        }));

        var content = new ByteArrayContent(GrpcWire.Frame(Encoding.UTF8.GetBytes(new string('a', 4096))));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc-web");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/{ServiceName}/Say") { Content = content };
        request.Headers.Add("grpc-accept-encoding", "identity,gzip");

        var response = await server.Client.SendAsync(request, Token);

        Assert.Equal("gzip", response.Headers.GetValues("grpc-encoding").Single());

        var (messages, trailers) = GrpcWire.Parse(await response.Content.ReadAsByteArrayAsync(Token), out var flags);

        Assert.Equal("0", trailers["grpc-status"]);
        Assert.Equal(1, flags.Single());

        // Actually gzip, and actually smaller than what it carries.
        using var gzip = new System.IO.Compression.GZipStream(
            new MemoryStream(messages.Single()),
            System.IO.Compression.CompressionMode.Decompress
        );
        using var inflated = new MemoryStream();
        await gzip.CopyToAsync(inflated, Token);

        Assert.Equal(new string('a', 4096), Encoding.UTF8.GetString(inflated.ToArray()));
        Assert.True(messages.Single().Length < 4096);
    }

    [Fact]
    public async Task Rejects_a_content_type_it_does_not_speak()
    {
        await using var server = await StartAsync();

        var response = await PostAsync(server, "Say", "application/json", "{}"u8.ToArray());

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    static Task<HttpResponseMessage> PostAsync(TestServer server, string method, string contentType, byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        return server.Client.PostAsync($"/{ServiceName}/{method}", content, Token);
    }

}

/// <summary>Hand-rolled framing, so the tests agree with the specification rather than with the server.</summary>
static class GrpcWire
{
    public static byte[] Frame(byte[] payload)
    {
        var frame = new byte[5 + payload.Length];
        frame[0] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(5));

        return frame;
    }

    public static (List<byte[]> Messages, Dictionary<string, string> Trailers) Parse(byte[] body)
        => Parse(body, out _);

    public static (List<byte[]> Messages, Dictionary<string, string> Trailers) Parse(byte[] body, out List<byte> messageFlags)
    {
        messageFlags = [];
        var messages = new List<byte[]>();
        var trailers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;

        while (offset + 5 <= body.Length)
        {
            var flag = body[offset];
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(offset + 1));
            var payload = body.AsSpan(offset + 5, length).ToArray();
            offset += 5 + length;

            if ((flag & 0x80) != 0)
            {
                foreach (var line in Encoding.ASCII.GetString(payload).Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                {
                    var separator = line.IndexOf(':');
                    trailers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                }
            }
            else
            {
                messages.Add(payload);
                messageFlags.Add(flag);
            }
        }

        return (messages, trailers);
    }
}

/// <summary>
/// The two string formats gRPC defines for itself, and the content-type sniffing that decides which
/// framing a call gets. All three are places where being nearly right is indistinguishable from
/// being wrong until a real client shows up.
/// </summary>
public class GrpcProtocolTests
{
    [Theory]
    [InlineData("100m", 100)]
    [InlineData("2S", 2000)]
    [InlineData("1M", 60_000)]
    [InlineData("1H", 3_600_000)]
    public void Parses_a_timeout(string value, double expectedMilliseconds)
        => Assert.Equal(expectedMilliseconds, GrpcProtocol.ParseTimeout(value)!.Value.TotalMilliseconds);

    [Theory]
    [InlineData("1000u", 1)]
    [InlineData("1000000n", 1)]
    public void Parses_sub_millisecond_units(string value, double expectedMilliseconds)
        => Assert.Equal(expectedMilliseconds, GrpcProtocol.ParseTimeout(value)!.Value.TotalMilliseconds);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("100")]
    [InlineData("abcm")]
    [InlineData("100x")]
    [InlineData("1234567890S")]
    public void Treats_an_unusable_timeout_as_no_deadline(string? value)
        => Assert.Null(GrpcProtocol.ParseTimeout(value));

    [Fact]
    public void Leaves_a_printable_message_alone()
        => Assert.Equal("no such order", GrpcProtocol.EscapeMessage("no such order"));

    [Fact]
    public void Escapes_what_a_header_cannot_carry()
    {
        // A newline in an exception message would otherwise forge a second header field. A space is
        // printable ASCII and stays as it is — the message is a header value, not a header line.
        Assert.Equal("a%0D%0Ax-evil: yes", GrpcProtocol.EscapeMessage("a\r\nx-evil: yes"));
        Assert.Equal("100%25", GrpcProtocol.EscapeMessage("100%"));
        Assert.Equal("caf%C3%A9", GrpcProtocol.EscapeMessage("café"));
    }

    [Theory]
    [InlineData("application/grpc", "Grpc")]
    [InlineData("application/grpc+proto", "Grpc")]
    [InlineData("application/grpc+json; charset=utf-8", "Grpc")]
    [InlineData("APPLICATION/GRPC", "Grpc")]
    [InlineData("application/grpc-web", "GrpcWeb")]
    [InlineData("application/grpc-web+proto", "GrpcWeb")]
    [InlineData("application/grpc-web-text", "GrpcWebText")]
    [InlineData("application/grpc-web-text+proto", "GrpcWebText")]
    public void Recognises_each_framing(string contentType, string expected)
    {
        Assert.True(GrpcProtocol.TryParseContentType(contentType, out var kind));
        Assert.Equal(expected, kind.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("application/json")]
    [InlineData("application/grpcish")]
    [InlineData("text/plain")]
    public void Rejects_anything_else(string? contentType)
        => Assert.False(GrpcProtocol.TryParseContentType(contentType, out _));

    [Theory]
    [InlineData("identity,gzip", "gzip", true)]
    [InlineData("identity, gzip, deflate", "deflate", true)]
    [InlineData("GZIP", "gzip", true)]
    [InlineData("identity", "gzip", false)]
    [InlineData("", "gzip", false)]
    [InlineData(null, "gzip", false)]
    public void Reads_an_accept_encoding_list(string? accepted, string encoding, bool expected)
        => Assert.Equal(expected, GrpcProtocol.Accepts(accepted, encoding));
}

using System.IO.Pipelines;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

namespace Shiny.Net.HttpServer.Tests;

public record Numbers(int Left, int Right);

[JsonSerializable(typeof(Numbers))]
public partial class BodyInterceptionJsonContext : JsonSerializerContext;

/// <summary>
/// The two seams a middleware needs to see a body it did not write: a settable
/// <see cref="HttpRequest.Body"/> on the way in, and a wrappable <see cref="IResponseBodyControl"/>
/// on the way out. Response compression already rides the second one; a traffic recorder or a
/// request logger rides both.
/// </summary>
public class BodyInterceptionTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- the middleware under test ----

    /// <summary>
    /// Buffers the request body so it can be read twice, and tees the response body into a second
    /// buffer as it goes to the wire. Both captures land in <paramref name="log"/>.
    /// </summary>
    sealed class RecordingMiddleware(Recording log) : IHttpMiddleware
    {
        public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
        {
            if (context.Request.HasBody)
            {
                var buffered = new MemoryStream();
                await context.Request.Body.CopyToAsync(buffered, context.RequestAborted);
                buffered.Position = 0;

                log.RequestBody = Encoding.UTF8.GetString(buffered.ToArray());

                // handed on rewound, so the handler reads the same bytes the recorder just did
                context.Request.Body = buffered;
            }

            var tee = new TeeBodyControl(context.Response.BodyControl, log.ResponseBody);
            context.Response.Bind(tee);

            try
            {
                await next(context);
            }
            finally
            {
                // The connection completes its own producer rather than whatever the response ended
                // up bound to, so anything still sitting in this wrapper's writer would never reach
                // the wire. Same reason response compression flushes here.
                await tee.FinishAsync();
            }

            log.StatusCode = context.Response.StatusCode;
        }
    }

    sealed class Recording
    {
        public string? RequestBody { get; set; }
        public MemoryStream ResponseBody { get; } = new();
        public int StatusCode { get; set; }

        public string ResponseText => Encoding.UTF8.GetString(this.ResponseBody.ToArray());
    }

    /// <summary>
    /// Copies every body byte into <paramref name="capture"/> on its way to <paramref name="inner"/>.
    /// <see cref="Writer"/> is built over <see cref="Stream"/> rather than over the inner writer so
    /// both write paths meet in one place and nothing is captured twice or not at all.
    /// </summary>
    sealed class TeeBodyControl(IResponseBodyControl inner, Stream capture) : IResponseBodyControl
    {
        Stream? stream;
        PipeWriter? writer;
        bool finished;

        public bool HasStarted => inner.HasStarted;

        public Stream Stream => this.stream ??= new TeeStream(inner.Stream, capture);

        public PipeWriter Writer => this.writer ??= PipeWriter.Create(
            this.Stream,
            new StreamPipeWriterOptions(leaveOpen: true)
        );

        public ValueTask StartAsync(CancellationToken cancellationToken) => inner.StartAsync(cancellationToken);

        public ValueTask CompleteAsync(CancellationToken cancellationToken) => inner.CompleteAsync(cancellationToken);

        public async ValueTask FinishAsync()
        {
            if (this.finished)
                return;

            this.finished = true;

            if (this.writer is { } pending)
                await pending.FlushAsync(CancellationToken.None);
        }

        sealed class TeeStream(Stream inner, Stream capture) : Stream
        {
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
                => this.Write(new ReadOnlySpan<byte>(buffer, offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                capture.Write(buffer);
                inner.Write(buffer);
            }

            public override async ValueTask WriteAsync(
                ReadOnlyMemory<byte> buffer,
                CancellationToken cancellationToken = default
            )
            {
                await capture.WriteAsync(buffer, cancellationToken);
                await inner.WriteAsync(buffer, cancellationToken);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => this.WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

            public override void Flush() => inner.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }

    // ---- request body ----

    [Fact]
    public async Task Request_body_is_readable_again_after_a_middleware_buffers_it()
    {
        var log = new Recording();

        await using var server = await TestServer.StartAsync(app =>
        {
            app.Use(new RecordingMiddleware(log));
            app.MapPost("/echo", async context =>
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync(context.RequestAborted);
                await context.Response.WriteAsync($"handler saw: {body}");
            });
        });

        var response = await server.Client.PostAsync(
            "/echo",
            new StringContent("the quick brown fox"),
            Token
        );

        Assert.Equal("handler saw: the quick brown fox", await response.Content.ReadAsStringAsync(Token));
        Assert.Equal("the quick brown fox", log.RequestBody);
    }

    [Fact]
    public async Task Setting_the_body_discards_a_reader_handed_out_over_the_old_stream()
    {
        var request = new HttpContext().Request;
        request.Body = new MemoryStream("first"u8.ToArray());

        var before = request.BodyReader;

        request.Body = new MemoryStream("second"u8.ToArray());

        Assert.NotSame(before, request.BodyReader);

        var read = await request.BodyReader.ReadAsync(Token);
        Assert.Equal("second", Encoding.UTF8.GetString(System.Buffers.BuffersExtensions.ToArray(read.Buffer)));
    }

    [Fact]
    public async Task Buffered_request_body_still_binds_to_a_typed_endpoint()
    {
        var log = new Recording();

        await using var server = await TestServer.StartAsync(app =>
        {
            app.Use(new RecordingMiddleware(log));
            app.MapPost("/sum", async context =>
            {
                var body = await context.Request.ReadJsonAsync(BodyInterceptionJsonContext.Default.Numbers);
                await context.Response.WriteAsync((body!.Left + body.Right).ToString());
            });
        });

        var response = await server.Client.PostAsJsonAsync(
            "/sum",
            new Numbers(2, 40),
            BodyInterceptionJsonContext.Default.Numbers,
            Token
        );

        Assert.Equal("42", await response.Content.ReadAsStringAsync(Token));
        Assert.Contains("\"Left\":2", log.RequestBody);
    }

    // ---- response body ----

    [Fact]
    public async Task Wrapped_control_captures_a_body_written_through_the_pipe_writer()
    {
        var log = new Recording();

        await using var server = await TestServer.StartAsync(app =>
        {
            app.Use(new RecordingMiddleware(log));

            // WriteAsync goes through BodyWriter, which is the path the convenience helpers take
            app.MapGet("/text", context => context.Response.WriteAsync("hello from the handler"));
        });

        var response = await server.Client.GetAsync("/text", Token);

        Assert.Equal("hello from the handler", await response.Content.ReadAsStringAsync(Token));
        Assert.Equal("hello from the handler", log.ResponseText);
        Assert.Equal(StatusCodes.Status200OK, log.StatusCode);
    }

    [Fact]
    public async Task Wrapped_control_captures_a_body_written_through_the_stream()
    {
        var log = new Recording();

        await using var server = await TestServer.StartAsync(app =>
        {
            app.Use(new RecordingMiddleware(log));
            app.MapGet("/stream", async context =>
            {
                context.Response.ContentType = "text/plain";
                await using var writer = new StreamWriter(context.Response.Body, leaveOpen: true);
                await writer.WriteAsync("streamed straight to Body");
            });
        });

        var response = await server.Client.GetAsync("/stream", Token);

        Assert.Equal("streamed straight to Body", await response.Content.ReadAsStringAsync(Token));
        Assert.Equal("streamed straight to Body", log.ResponseText);
    }

    [Fact]
    public async Task Wrapped_control_captures_an_error_response_it_did_not_produce()
    {
        var log = new Recording();

        await using var server = await TestServer.StartAsync(app =>
        {
            app.Use(new RecordingMiddleware(log));
            app.MapGet("/gone", async context =>
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync("no such thing");
            });
        });

        await server.Client.GetAsync("/gone", Token);

        Assert.Equal(StatusCodes.Status404NotFound, log.StatusCode);
        Assert.Equal("no such thing", log.ResponseText);
    }

    [Fact]
    public async Task Wrapping_leaves_a_body_of_no_bytes_alone()
    {
        var log = new Recording();

        await using var server = await TestServer.StartAsync(app =>
        {
            app.Use(new RecordingMiddleware(log));
            app.MapGet("/nothing", context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return default;
            });
        });

        var response = await server.Client.GetAsync("/nothing", Token);

        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("", log.ResponseText);
    }
}

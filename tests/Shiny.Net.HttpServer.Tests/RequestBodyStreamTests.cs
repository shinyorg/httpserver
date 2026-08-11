using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Shiny.Net.HttpServer.Http1;

namespace Shiny.Net.HttpServer.Tests;

public class RequestBodyStreamTests
{
    /// <summary>Feeds bytes through a real pipe, so the stream sees the same shape it does live.</summary>
    static async Task<PipeReader> PipeOf(string content, bool complete = true)
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(Encoding.ASCII.GetBytes(content));

        if (complete)
            await pipe.Writer.CompleteAsync();

        return pipe.Reader;
    }

    static async Task<string> ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    [Fact]
    public async Task Empty_stream_reads_nothing()
        => Assert.Equal(string.Empty, await ReadAll(EmptyReadStream.Instance));

    [Fact]
    public async Task Content_length_stream_reads_exactly_its_length()
    {
        var reader = await PipeOf("hello worldEXTRA");
        var stream = new ContentLengthReadStream(reader, 11);

        Assert.Equal("hello world", await ReadAll(stream));
    }

    [Fact]
    public async Task Content_length_stream_reads_across_several_calls()
    {
        var reader = await PipeOf("abcdefghij");
        var stream = new ContentLengthReadStream(reader, 10);
        var buffer = new byte[4];

        Assert.Equal(4, await stream.ReadAsync(buffer, TestContext.Current.CancellationToken));
        Assert.Equal("abcd", Encoding.ASCII.GetString(buffer, 0, 4));
        Assert.Equal(4, await stream.ReadAsync(buffer, TestContext.Current.CancellationToken));
        Assert.Equal(2, await stream.ReadAsync(buffer, TestContext.Current.CancellationToken));
        Assert.Equal(0, await stream.ReadAsync(buffer, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Content_length_stream_throws_when_the_body_is_truncated()
    {
        var reader = await PipeOf("short");
        var stream = new ContentLengthReadStream(reader, 100);

        await Assert.ThrowsAsync<BadHttpRequestException>(async () => await ReadAll(stream));
    }

    [Fact]
    public async Task Chunked_stream_reassembles_chunks()
    {
        var reader = await PipeOf("5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n");
        var stream = new ChunkedReadStream(reader, null);

        Assert.Equal("hello world", await ReadAll(stream));
    }

    [Fact]
    public async Task Chunked_stream_handles_a_single_terminating_chunk()
    {
        var reader = await PipeOf("0\r\n\r\n");
        Assert.Equal(string.Empty, await ReadAll(new ChunkedReadStream(reader, null)));
    }

    [Fact]
    public async Task Chunked_stream_ignores_chunk_extensions()
    {
        var reader = await PipeOf("5;name=value\r\nhello\r\n0\r\n\r\n");
        Assert.Equal("hello", await ReadAll(new ChunkedReadStream(reader, null)));
    }

    [Fact]
    public async Task Chunked_stream_accepts_trailers()
    {
        var reader = await PipeOf("5\r\nhello\r\n0\r\nX-Checksum: abc\r\n\r\n");
        Assert.Equal("hello", await ReadAll(new ChunkedReadStream(reader, null)));
    }

    [Fact]
    public async Task Chunked_stream_reads_hex_chunk_sizes()
    {
        var payload = new string('x', 255);
        var reader = await PipeOf($"ff\r\n{payload}\r\n0\r\n\r\n");

        Assert.Equal(payload, await ReadAll(new ChunkedReadStream(reader, null)));
    }

    [Fact]
    public async Task Chunked_stream_enforces_the_body_size_limit()
    {
        var reader = await PipeOf("5\r\nhello\r\n5\r\nworld\r\n0\r\n\r\n");
        var stream = new ChunkedReadStream(reader, maxBodySize: 6);

        var ex = await Assert.ThrowsAsync<BadHttpRequestException>(async () => await ReadAll(stream));
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, ex.StatusCode);
    }

    [Theory]
    [InlineData("zz\r\nhello\r\n0\r\n\r\n")]
    [InlineData("5\r\nhello")]
    [InlineData("5\r\nhelloXX0\r\n\r\n")]
    public async Task Chunked_stream_rejects_malformed_framing(string raw)
    {
        var reader = await PipeOf(raw);
        await Assert.ThrowsAsync<BadHttpRequestException>(async () => await ReadAll(new ChunkedReadStream(reader, null)));
    }

    [Fact]
    public async Task Draining_consumes_an_unread_body_so_the_connection_can_be_reused()
    {
        var reader = await PipeOf("hello worldNEXT", complete: false);
        var stream = new ContentLengthReadStream(reader, 11);

        Assert.True(await stream.TryDrainAsync(TestContext.Current.CancellationToken));

        // Only the body was consumed; whatever followed is still there for the next request.
        var result = await reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("NEXT", Encoding.ASCII.GetString(result.Buffer.ToArray()));
    }

    [Fact]
    public async Task Draining_an_already_read_body_succeeds()
    {
        var reader = await PipeOf("hello world");
        var stream = new ContentLengthReadStream(reader, 11);

        await ReadAll(stream);
        Assert.True(await stream.TryDrainAsync(TestContext.Current.CancellationToken));
    }
}

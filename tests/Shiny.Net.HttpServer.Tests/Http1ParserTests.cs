using System.Buffers;
using System.Text;
using Shiny.Net.HttpServer.Http1;

namespace Shiny.Net.HttpServer.Tests;

public class Http1ParserTests
{
    static (bool Complete, HttpRequest Request) Parse(string raw, HttpServerLimits? limits = null)
    {
        var context = new HttpContext();
        var parser = new Http1RequestParser();
        var sequence = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes(raw));
        var reader = new SequenceReader<byte>(sequence);

        var complete = parser.TryParseRequestHead(ref reader, context.Request, limits ?? new HttpServerLimits());
        return (complete, context.Request);
    }

    [Fact]
    public void Parses_a_minimal_request()
    {
        var (complete, request) = Parse("GET /ping HTTP/1.1\r\nHost: localhost\r\n\r\n");

        Assert.True(complete);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/ping", request.Path);
        Assert.Equal("HTTP/1.1", request.Protocol);
        Assert.Equal("localhost", request.Headers.GetFirst("Host"));
    }

    [Fact]
    public void Interns_known_methods()
    {
        var (_, request) = Parse("POST /x HTTP/1.1\r\nHost: h\r\n\r\n");
        Assert.Same(HttpMethods.Post, request.Method);
    }

    [Fact]
    public void Splits_the_query_string_from_the_path()
    {
        var (_, request) = Parse("GET /search?q=hello&page=2 HTTP/1.1\r\nHost: h\r\n\r\n");

        Assert.Equal("/search", request.Path);
        Assert.Equal("?q=hello&page=2", request.QueryString);
        Assert.Equal("hello", request.Query.GetFirst("q"));
        Assert.Equal("2", request.Query.GetFirst("page"));
        Assert.Equal("/search?q=hello&page=2", request.RawTarget);
    }

    [Fact]
    public void Percent_decodes_the_path()
    {
        var (_, request) = Parse("GET /files/my%20file.txt HTTP/1.1\r\nHost: h\r\n\r\n");
        Assert.Equal("/files/my file.txt", request.Path);
    }

    [Fact]
    public void Decodes_plus_as_space_in_the_query_only()
    {
        var (_, request) = Parse("GET /a+b?q=a+b HTTP/1.1\r\nHost: h\r\n\r\n");

        // '+' is a form-encoding convention, not a path one. Conflating them silently corrupts
        // any path that legitimately contains a plus.
        Assert.Equal("/a+b", request.Path);
        Assert.Equal("a b", request.Query.GetFirst("q"));
    }

    [Fact]
    public void Collects_repeated_headers()
    {
        var (_, request) = Parse("GET / HTTP/1.1\r\nHost: h\r\nX-Tag: one\r\nX-Tag: two\r\n\r\n");
        Assert.Equal(2, request.Headers["X-Tag"].Count);
    }

    [Fact]
    public void Trims_optional_whitespace_around_header_values()
    {
        var (_, request) = Parse("GET / HTTP/1.1\r\nHost:    spaced   \r\n\r\n");
        Assert.Equal("spaced", request.Headers.GetFirst("Host"));
    }

    [Fact]
    public void Matches_header_names_case_insensitively()
    {
        var (_, request) = Parse("GET / HTTP/1.1\r\nCONTENT-TYPE: text/plain\r\nHost: h\r\n\r\n");
        Assert.Equal("text/plain", request.ContentType);
    }

    [Fact]
    public void Reports_incomplete_when_the_head_is_cut_short()
    {
        var (complete, _) = Parse("GET /ping HTTP/1.1\r\nHost: local");
        Assert.False(complete);
    }

    [Fact]
    public void Parses_a_head_delivered_across_two_reads()
    {
        var context = new HttpContext();
        var parser = new Http1RequestParser();

        var first = new SequenceReader<byte>(new ReadOnlySequence<byte>("GET /ping HTTP/1.1\r\nHo"u8.ToArray()));
        Assert.False(parser.TryParseRequestHead(ref first, context.Request, new HttpServerLimits()));

        // Only whole lines are consumed. The request line is gone; the partial "Ho" is still
        // unconsumed, so the connection replays it at the head of the next read.
        Assert.Equal("GET /ping HTTP/1.1\r\n".Length, first.Consumed);
        Assert.Equal("/ping", context.Request.Path);

        var second = new SequenceReader<byte>(new ReadOnlySequence<byte>("Host: localhost\r\n\r\n"u8.ToArray()));
        Assert.True(parser.TryParseRequestHead(ref second, context.Request, new HttpServerLimits()));
        Assert.Equal("localhost", context.Request.Headers.GetFirst("Host"));
    }

    [Theory]
    [InlineData("GET\r\n\r\n")]
    [InlineData("GET /ping\r\n\r\n")]
    [InlineData("GET /ping HTTP/9.9\r\n\r\n")]
    [InlineData("GET ping HTTP/1.1\r\n\r\n")]
    [InlineData("GET /ping HTTP/1.1\r\nNoColonHere\r\n\r\n")]
    [InlineData("GET /ping HTTP/1.1\r\n: novalue\r\n\r\n")]
    [InlineData("GET /ping HTTP/1.1\r\nBad Name: x\r\n\r\n")]
    public void Rejects_malformed_requests(string raw)
        => Assert.Throws<BadHttpRequestException>(() => Parse(raw));

    [Fact]
    public void Enforces_the_request_line_limit()
    {
        var limits = new HttpServerLimits { MaxRequestLineSize = 32 };
        var raw = "GET /" + new string('a', 100) + " HTTP/1.1\r\nHost: h\r\n\r\n";

        var ex = Assert.Throws<BadHttpRequestException>(() => Parse(raw, limits));
        Assert.Equal(StatusCodes.Status414UriTooLong, ex.StatusCode);
    }

    [Fact]
    public void Enforces_the_header_count_limit()
    {
        var limits = new HttpServerLimits { MaxRequestHeaderCount = 2 };
        var raw = new StringBuilder("GET / HTTP/1.1\r\nHost: h\r\n");
        for (var i = 0; i < 5; i++)
            raw.Append($"X-{i}: v\r\n");
        raw.Append("\r\n");

        Assert.Throws<BadHttpRequestException>(() => Parse(raw.ToString(), limits));
    }

    [Fact]
    public void Enforces_the_total_header_size_limit()
    {
        var limits = new HttpServerLimits { MaxRequestHeadersTotalSize = 64 };
        var raw = "GET / HTTP/1.1\r\nHost: h\r\nX-Big: " + new string('a', 200) + "\r\n\r\n";

        Assert.Throws<BadHttpRequestException>(() => Parse(raw, limits));
    }

    [Fact]
    public void Accepts_HTTP_1_0()
    {
        var (complete, request) = Parse("GET / HTTP/1.0\r\n\r\n");

        Assert.True(complete);
        Assert.Equal(HttpProtocols.Http10, request.Protocol);
    }
}

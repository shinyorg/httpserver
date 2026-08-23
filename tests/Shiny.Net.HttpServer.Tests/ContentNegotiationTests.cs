using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Negotiation;

namespace Shiny.Net.HttpServer.Tests;

public record Reading(string Sensor, double Value);

[JsonSerializable(typeof(Reading))]
public partial class NegotiationJsonContext : JsonSerializerContext;

public class MediaTypeRangeTests
{
    [Fact]
    public void Parses_a_simple_accept_header()
    {
        var ranges = MediaTypeRange.ParseAccept("application/json");

        Assert.Single(ranges);
        Assert.Equal("application", ranges[0].Type);
        Assert.Equal("json", ranges[0].SubType);
        Assert.Equal(1, ranges[0].Quality);
    }

    [Fact]
    public void Orders_by_quality()
    {
        var ranges = MediaTypeRange.ParseAccept("text/plain;q=0.3, application/json;q=0.9, text/html;q=0.5");

        Assert.Equal("json", ranges[0].SubType);
        Assert.Equal("html", ranges[1].SubType);
        Assert.Equal("plain", ranges[2].SubType);
    }

    /// <summary>
    /// A browser sends a trailing <c>*/*;q=0.8</c>. Without the specificity tie-break it would rank
    /// alongside the type the client actually asked for.
    /// </summary>
    [Fact]
    public void Ranks_a_specific_type_above_a_wildcard_at_the_same_quality()
    {
        var ranges = MediaTypeRange.ParseAccept("*/*, text/html, text/*");

        Assert.Equal(("text", "html"), (ranges[0].Type, ranges[0].SubType));
        Assert.Equal(("text", "*"), (ranges[1].Type, ranges[1].SubType));
        Assert.Equal(("*", "*"), (ranges[2].Type, ranges[2].SubType));
    }

    [Fact]
    public void Treats_an_absent_header_as_anything_goes()
    {
        foreach (var header in new[] { null, "", "   " })
        {
            var ranges = MediaTypeRange.ParseAccept(header);

            Assert.Single(ranges);
            Assert.Equal(("*", "*"), (ranges[0].Type, ranges[0].SubType));
        }
    }

    [Theory]
    [InlineData("application/json", "application/json", true)]
    [InlineData("application/*", "application/json", true)]
    [InlineData("*/*", "application/json", true)]
    [InlineData("text/*", "application/json", false)]
    [InlineData("application/xml", "application/json", false)]
    public void Matches_media_types(string range, string mediaType, bool expected)
    {
        var parsed = MediaTypeRange.ParseAccept(range)[0];

        Assert.Equal(expected, parsed.Matches(mediaType));
    }

    /// <summary>The client asked for a format, not an encoding.</summary>
    [Fact]
    public void Ignores_parameters_when_matching()
        => Assert.True(MediaTypeRange.ParseAccept("text/plain")[0].Matches("text/plain; charset=utf-8"));

    [Fact]
    public void Keeps_a_refusal_distinct_from_a_weak_preference()
    {
        var ranges = MediaTypeRange.ParseAccept("application/json;q=0, text/plain;q=0.1");

        Assert.Equal(0, ranges.Single(r => r.SubType == "json").Quality);
        Assert.Equal(0.1, ranges.Single(r => r.SubType == "plain").Quality);
    }
}

public class ContentNegotiationTests
{
    static Task<TestServer> StartAsync(Action<ContentNegotiationOptions>? configure = null) => TestServer.StartAsync(
        app =>
        {
            app.MapGet("/reading", _ => Results.Negotiate(new Reading("kitchen", 21.5)));
            app.MapGet("/count", _ => Results.Negotiate(42));
        },
        builder => builder.AddContentNegotiation(configure)
    );

    static async Task<HttpResponseMessage> GetAsync(TestServer server, string path, string? accept)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (accept is not null)
            request.Headers.TryAddWithoutValidation("Accept", accept);

        return await server.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public ContentNegotiationTests() => JsonTypeInfoRegistry.Register(NegotiationJsonContext.Default);

    [Fact]
    public async Task Serves_json_when_json_is_asked_for()
    {
        await using var server = await StartAsync();

        var response = await GetAsync(server, "/reading", "application/json");

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(
            "kitchen",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal
        );
    }

    /// <summary>The case that makes this worth having: a shell script wants a line, not a quoted string.</summary>
    [Fact]
    public async Task Serves_text_when_text_is_asked_for()
    {
        await using var server = await StartAsync();

        var response = await GetAsync(server, "/count", "text/plain");

        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("42", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Honours_a_quality_preference()
    {
        await using var server = await StartAsync();

        var text = await GetAsync(server, "/count", "application/json;q=0.2, text/plain;q=0.9");
        var json = await GetAsync(server, "/count", "application/json;q=0.9, text/plain;q=0.2");

        Assert.Equal("text/plain", text.Content.Headers.ContentType?.MediaType);
        Assert.Equal("application/json", json.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>A client with no preference gets the server's, which is JSON.</summary>
    [Fact]
    public async Task Defaults_to_json_for_a_wildcard()
    {
        await using var server = await StartAsync();

        Assert.Equal("application/json", (await GetAsync(server, "/count", "*/*")).Content.Headers.ContentType?.MediaType);
        Assert.Equal("application/json", (await GetAsync(server, "/count", null)).Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// Sending a format the caller said it cannot read fails later, further away and less clearly
    /// than saying so now.
    /// </summary>
    [Fact]
    public async Task Answers_406_when_nothing_is_acceptable()
    {
        await using var server = await StartAsync();

        var response = await GetAsync(server, "/reading", "application/xml");

        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Falls_back_instead_of_406_when_configured_to()
    {
        await using var server = await StartAsync(o => o.ReturnNotAcceptable = false);

        var response = await GetAsync(server, "/reading", "application/xml");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Uses_a_custom_formatter()
    {
        await using var server = await StartAsync(o => o.AddFormatter(
            "text/csv",
            (ctx, value, ct) => ctx.Response.WriteBytesAsync(
                Encoding.UTF8.GetBytes(value is Reading r ? $"{r.Sensor},{r.Value}" : string.Empty),
                cancellationToken: ct
            )
        ));

        var response = await GetAsync(server, "/reading", "text/csv");

        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("kitchen,21.5", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A shared cache that stored the JSON copy without Vary would hand it to a client that asked
    /// for text.
    /// </summary>
    [Fact]
    public async Task Announces_that_the_response_varies_by_accept()
    {
        await using var server = await StartAsync();

        var response = await GetAsync(server, "/count", "text/plain");

        Assert.Contains("Accept", response.Headers.Vary, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An object whose only text form is its type name would produce a response that looks
    /// successful and says nothing, so the text formatter declines it.
    /// </summary>
    [Fact]
    public async Task Does_not_offer_text_for_a_type_with_no_meaningful_string_form()
    {
        await using var server = await StartAsync();

        var response = await GetAsync(server, "/reading", "text/plain");

        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
    }

    [Fact]
    public void Selects_nothing_when_the_client_refuses_everything_on_offer()
    {
        var options = new ContentNegotiationOptions();
        var context = new HttpContext();

        context.Request.Headers.Set(HeaderNames.Accept, "application/json;q=0, text/plain;q=0");

        Assert.Null(options.Select(context.Request, 42));
    }
}

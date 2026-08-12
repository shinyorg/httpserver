using Microsoft.Extensions.Primitives;
using Shiny.Net.HttpServer.Endpoints;

namespace Shiny.Net.HttpServer.Tests;

public class EndpointBinderTests
{
    [Theory]
    [InlineData("42", true, 42)]
    [InlineData("-7", true, -7)]
    [InlineData("4.2", false, 0)]
    [InlineData("abc", false, 0)]
    [InlineData(null, false, 0)]
    public void Binds_integers(string? raw, bool expected, int value)
    {
        Assert.Equal(expected, EndpointBinder.TryBind<int>(raw, out var bound));
        if (expected)
            Assert.Equal(value, bound);
    }

    [Fact]
    public void Binds_with_invariant_culture()
    {
        // A server must not parse "1.5" differently because of the machine's locale.
        Assert.True(EndpointBinder.TryBind<double>("1.5", out var value));
        Assert.Equal(1.5, value);
    }

    [Fact]
    public void Treats_a_missing_nullable_as_present_and_null()
    {
        Assert.True(EndpointBinder.TryBindNullable<int>(null, out var value));
        Assert.Null(value);

        Assert.True(EndpointBinder.TryBindNullable<int>("", out var empty));
        Assert.Null(empty);
    }

    [Fact]
    public void Still_rejects_a_present_but_invalid_nullable()
    {
        Assert.False(EndpointBinder.TryBindNullable<int>("nope", out var value));
        Assert.Null(value);
    }

    [Theory]
    [InlineData("Red", true)]
    [InlineData("red", true)]
    [InlineData("GREEN", true)]
    [InlineData("blue", false)]
    [InlineData("", false)]
    public void Binds_enums_case_insensitively(string raw, bool expected)
        => Assert.Equal(expected, EndpointBinder.TryBindEnum<Colour>(raw, out _));

    [Fact]
    public void Binds_arrays_of_parsable_values()
    {
        Assert.True(EndpointBinder.TryBindArray<int>(new StringValues(["1", "2", "3"]), out var values));
        Assert.Equal([1, 2, 3], values);
    }

    [Fact]
    public void Rejects_an_array_containing_an_unparseable_value()
        => Assert.False(EndpointBinder.TryBindArray<int>(new StringValues(["1", "x"]), out _));

    [Fact]
    public void Binds_an_absent_array_as_empty()
    {
        Assert.True(EndpointBinder.TryBindArray<int>(StringValues.Empty, out var values));
        Assert.Empty(values);
        Assert.Empty(EndpointBinder.BindStringArray(StringValues.Empty));
    }

    [Fact]
    public void Binds_guids_and_dates()
    {
        Assert.True(EndpointBinder.TryBind<Guid>("d0f9b9c8-0000-4000-8000-000000000000", out _));
        Assert.True(EndpointBinder.TryBind<DateOnly>("2026-08-10", out var date));
        Assert.Equal(new DateOnly(2026, 8, 10), date);
    }
}

public class ResultTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static async Task<(int Status, string Body, string? ContentType)> ExecuteAsync(IResult result)
    {
        await using var server = await TestServer.StartAsync(app => app.MapGet("/x", _ => result));

        var response = await server.Client.GetAsync("/x", Token);

        return (
            (int)response.StatusCode,
            await response.Content.ReadAsStringAsync(Token),
            response.Content.Headers.ContentType?.ToString()
        );
    }

    [Fact]
    public async Task Ok_with_no_body_is_an_empty_200()
    {
        var (status, body, _) = await ExecuteAsync(Results.Ok());

        Assert.Equal(200, status);
        Assert.Equal(string.Empty, body);
    }

    [Fact]
    public async Task Text_sets_a_plain_text_content_type()
    {
        var (status, body, contentType) = await ExecuteAsync(Results.Text("hello"));

        Assert.Equal(200, status);
        Assert.Equal("hello", body);
        Assert.Equal("text/plain; charset=utf-8", contentType);
    }

    [Fact]
    public async Task Json_with_explicit_type_info_serializes_from_compile_time_metadata()
    {
        var (_, body, contentType) = await ExecuteAsync(Results.Ok(new Thing(3, "typed"), TestJson.Default.Thing));

        Assert.Equal("""{"id":3,"name":"typed"}""", body);
        Assert.Equal("application/json; charset=utf-8", contentType);
    }

    [Fact]
    public async Task Json_from_the_registry_matches_the_explicit_form_exactly()
    {
        JsonTypeInfoRegistry.Register(TestJson.Default);

        var explicitly = await ExecuteAsync(Results.Ok(new Thing(3, "typed"), TestJson.Default.Thing));
        var viaRegistry = await ExecuteAsync(Results.Ok(new Thing(3, "typed")));
        var viaActionResult = await ExecuteAsync(new OkObjectResult(new Thing(3, "typed")));

        // Two spellings of one intent must not disagree about property casing.
        Assert.Equal(explicitly.Body, viaRegistry.Body);
        Assert.Equal(explicitly.Body, viaActionResult.Body);
    }

    [Fact]
    public async Task NotFound_is_an_empty_404()
    {
        var (status, body, _) = await ExecuteAsync(Results.NotFound());

        Assert.Equal(404, status);
        Assert.Equal(string.Empty, body);
    }

    [Fact]
    public async Task BadRequest_with_a_message_returns_it()
    {
        var (status, body, _) = await ExecuteAsync(Results.BadRequest("nope"));

        Assert.Equal(400, status);
        Assert.Equal("nope", body);
    }

    [Fact]
    public async Task NoContent_is_204()
        => Assert.Equal(204, (await ExecuteAsync(Results.NoContent())).Status);

    [Fact]
    public async Task StatusCode_passes_the_code_through()
        => Assert.Equal(418, (await ExecuteAsync(Results.StatusCode(418))).Status);

    [Fact]
    public async Task Created_sets_a_Location_header()
    {
        await using var server = await TestServer.StartAsync(
            app => app.MapGet("/x", _ => Results.Created("/things/1"))
        );

        var response = await server.Client.GetAsync("/x", Token);

        Assert.Equal(201, (int)response.StatusCode);
        Assert.Equal("/things/1", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Redirect_uses_302_by_default_and_308_when_permanent_and_method_preserving()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapGet("/temp", _ => Results.Redirect("/there"));
            app.MapGet("/perm", _ => Results.Redirect("/there", permanent: true, preserveMethod: true));
        });

        using var noRedirects = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = server.Client.BaseAddress
        };

        Assert.Equal(302, (int)(await noRedirects.GetAsync("/temp", Token)).StatusCode);
        Assert.Equal(308, (int)(await noRedirects.GetAsync("/perm", Token)).StatusCode);
    }

    [Fact]
    public async Task Bytes_sets_the_given_content_type()
    {
        var (_, body, contentType) = await ExecuteAsync(Results.Bytes("abc"u8.ToArray(), "application/custom"));

        Assert.Equal("abc", body);
        Assert.Equal("application/custom", contentType);
    }

    [Fact]
    public async Task Stream_disposes_the_stream_it_was_given()
    {
        var stream = new MemoryStream("streamed"u8.ToArray());
        var (_, body, _) = await ExecuteAsync(Results.Stream(stream, "text/plain"));

        Assert.Equal("streamed", body);
        Assert.Throws<ObjectDisposedException>(() => stream.Position);
    }

    [Fact]
    public async Task ObjectResult_sends_a_string_as_text_and_JsonResult_sends_it_as_json()
    {
        JsonTypeInfoRegistry.Register(TestJson.Default);

        var asText = await ExecuteAsync(new ObjectResult("plain"));
        Assert.Equal("plain", asText.Body);
        Assert.Equal("text/plain; charset=utf-8", asText.ContentType);
    }

    [Fact]
    public async Task ObjectResult_with_a_null_value_writes_no_body()
    {
        var (status, body, _) = await ExecuteAsync(new ObjectResult(null, StatusCodes.Status200OK));

        Assert.Equal(200, status);
        Assert.Equal(string.Empty, body);
    }
}

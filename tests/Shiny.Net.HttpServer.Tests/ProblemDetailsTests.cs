using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.Net.HttpServer.Tests;

public class ProblemDetailsWriterTests
{
    static JsonElement Serialize(ProblemDetails problem)
    {
        var buffer = new ArrayBufferWriter<byte>();
        ProblemDetailsWriter.Write(buffer, problem);

        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    [Fact]
    public void Writes_the_standard_members()
    {
        var json = Serialize(new ProblemDetails
        {
            Type = "https://example.com/problems/out-of-stock",
            Title = "Out of stock",
            Status = 409,
            Detail = "Only 2 left.",
            Instance = "/orders/42"
        });

        Assert.Equal("https://example.com/problems/out-of-stock", json.GetProperty("type").GetString());
        Assert.Equal("Out of stock", json.GetProperty("title").GetString());
        Assert.Equal(409, json.GetProperty("status").GetInt32());
        Assert.Equal("Only 2 left.", json.GetProperty("detail").GetString());
        Assert.Equal("/orders/42", json.GetProperty("instance").GetString());
    }

    /// <summary>A member that is absent means "no information", which is not the same as null.</summary>
    [Fact]
    public void Omits_members_that_were_never_set()
    {
        var json = Serialize(new ProblemDetails { Status = 500 });

        Assert.False(json.TryGetProperty("detail", out _));
        Assert.False(json.TryGetProperty("instance", out _));
        Assert.False(json.TryGetProperty("type", out _));
    }

    [Fact]
    public void Writes_validation_errors_in_the_expected_shape()
    {
        var problem = new ValidationProblemDetails(new Dictionary<string, string[]>
        {
            ["email"] = ["Required.", "Must be an address."],
            ["age"] = ["Must be positive."]
        })
        {
            Status = 400
        };

        var json = Serialize(problem);
        var errors = json.GetProperty("errors");

        Assert.Equal(2, errors.GetProperty("email").GetArrayLength());
        Assert.Equal("Required.", errors.GetProperty("email")[0].GetString());
        Assert.Equal("Must be positive.", errors.GetProperty("age")[0].GetString());
    }

    /// <summary>
    /// The extension bag is <c>object?</c>, and the writer walks it by hand precisely so nothing
    /// here needs reflection. Each supported shape is worth pinning.
    /// </summary>
    [Fact]
    public void Writes_every_supported_extension_shape()
    {
        var problem = new ProblemDetails { Status = 400 };

        problem.Extensions["text"] = "hello";
        problem.Extensions["flag"] = true;
        problem.Extensions["count"] = 42;
        problem.Extensions["ratio"] = 1.5;
        problem.Extensions["missing"] = null;
        problem.Extensions["id"] = Guid.Empty;
        problem.Extensions["when"] = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        problem.Extensions["list"] = new[] { "a", "b" };
        problem.Extensions["nested"] = new Dictionary<string, object?> { ["inner"] = 7 };

        var json = Serialize(problem);

        Assert.Equal("hello", json.GetProperty("text").GetString());
        Assert.True(json.GetProperty("flag").GetBoolean());
        Assert.Equal(42, json.GetProperty("count").GetInt32());
        Assert.Equal(1.5, json.GetProperty("ratio").GetDouble());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("missing").ValueKind);
        Assert.Equal(Guid.Empty, json.GetProperty("id").GetGuid());
        Assert.Equal("2026-08-10T12:00:00+00:00", json.GetProperty("when").GetString());
        Assert.Equal(2, json.GetProperty("list").GetArrayLength());
        Assert.Equal(7, json.GetProperty("nested").GetProperty("inner").GetInt32());
    }

    /// <summary>A string is a sequence of characters, and writing one as an array would be a bug.</summary>
    [Fact]
    public void Writes_a_string_extension_as_a_string()
    {
        var problem = new ProblemDetails { Status = 400 };
        problem.Extensions["text"] = "abc";

        Assert.Equal(JsonValueKind.String, Serialize(problem).GetProperty("text").ValueKind);
    }

    [Fact]
    public void Falls_back_to_the_string_form_for_unsupported_extensions()
    {
        var problem = new ProblemDetails { Status = 400 };
        problem.Extensions["thing"] = new Version(1, 2, 3);

        Assert.Equal("1.2.3", Serialize(problem).GetProperty("thing").GetString());
    }

    /// <summary>A duplicate key would be the extension's, and the standard meaning has to win.</summary>
    [Fact]
    public void Ignores_an_extension_that_collides_with_a_standard_member()
    {
        var problem = new ProblemDetails { Status = 400, Title = "Real title" };
        problem.Extensions["title"] = "Impostor";

        var json = Serialize(problem);

        Assert.Equal("Real title", json.GetProperty("title").GetString());
    }

    [Fact]
    public void Leaves_non_ascii_readable()
    {
        var problem = new ProblemDetails { Status = 400, Detail = "Ungültige Eingabe — 값이 잘못되었습니다" };

        var buffer = new ArrayBufferWriter<byte>();
        ProblemDetailsWriter.Write(buffer, problem);

        Assert.Contains("Ungültige", Encoding.UTF8.GetString(buffer.WrittenSpan), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(404, "https://tools.ietf.org/html/rfc9110#section-15.5.5")]
    [InlineData(500, "https://tools.ietf.org/html/rfc9110#section-15.6.1")]
    [InlineData(599, "about:blank")]
    public void Defaults_the_type_from_the_status(int status, string expected)
        => Assert.Equal(expected, ProblemDetailsDefaults.GetTypeUri(status));

    [Fact]
    public void Defaults_the_title_and_status()
    {
        var problem = new ProblemDetails();
        ProblemDetailsDefaults.ApplyDefaults(problem, context: null, fallbackStatusCode: 418);

        Assert.Equal(418, problem.Status);
        Assert.Equal(StatusCodes.GetReasonPhrase(418), problem.Title);
    }

    [Fact]
    public void Gives_a_validation_problem_its_own_title()
    {
        var problem = new ValidationProblemDetails();
        ProblemDetailsDefaults.ApplyDefaults(problem, context: null, fallbackStatusCode: 400);

        Assert.Equal("One or more validation errors occurred.", problem.Title);
    }
}

public class ProblemDetailsEndToEndTests
{
    static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task Serves_a_problem_from_a_handler()
    {
        await using var server = await TestServer.StartAsync(app => app.MapGet(
            "/conflict",
            _ => Results.Problem(
                StatusCodes.Status409Conflict,
                detail: "Already paired.",
                type: "https://example.com/problems/already-paired"
            )
        ));

        var response = await server.Client.GetAsync("/conflict", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await ReadProblemAsync(response);

        Assert.Equal("https://example.com/problems/already-paired", problem.GetProperty("type").GetString());
        Assert.Equal("Already paired.", problem.GetProperty("detail").GetString());
        Assert.Equal(409, problem.GetProperty("status").GetInt32());

        // Filled in from the request, which is the one part a handler cannot supply.
        Assert.Equal("/conflict", problem.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task Serves_a_validation_problem()
    {
        await using var server = await TestServer.StartAsync(app => app.MapPost(
            "/orders",
            _ => Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["quantity"] = ["Must be greater than zero."]
            })
        ));

        var response = await server.Client.PostAsync("/orders", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemAsync(response);

        Assert.Equal(
            "Must be greater than zero.",
            problem.GetProperty("errors").GetProperty("quantity")[0].GetString()
        );
    }

    /// <summary>A 500 must not leak the exception message; that is the whole reason for the split.</summary>
    [Fact]
    public async Task Turns_an_unhandled_exception_into_a_problem_without_leaking_it()
    {
        await using var server = await TestServer.StartAsync(
            app => app.MapGet("/boom", _ => throw new InvalidOperationException("connection string is Server=secret")),
            builder => builder.AddProblemDetails()
        );

        var response = await server.Client.GetAsync("/boom", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(500, JsonDocument.Parse(body).RootElement.GetProperty("status").GetInt32());
    }

    /// <summary>
    /// A malformed argument is the caller's mistake, and a 500 would send them looking for a server
    /// fault that is not there.
    /// </summary>
    [Fact]
    public async Task Maps_a_client_mistake_to_a_4xx_and_says_what_it_was()
    {
        await using var server = await TestServer.StartAsync(
            app => app.MapGet("/bad", _ => throw new ArgumentException("id must be positive")),
            builder => builder.AddProblemDetails()
        );

        var response = await server.Client.GetAsync("/bad", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("id must be positive", (await ReadProblemAsync(response)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Honours_an_explicit_exception_mapping()
    {
        await using var server = await TestServer.StartAsync(
            app => app.MapGet("/gone", _ => throw new MissingThingException()),
            builder => builder.AddProblemDetails(o => o.MapException<MissingThingException>(StatusCodes.Status404NotFound))
        );

        var response = await server.Client.GetAsync("/gone", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Includes_exception_details_when_asked_to()
    {
        await using var server = await TestServer.StartAsync(
            app => app.MapGet("/boom", _ => throw new InvalidOperationException("the real reason")),
            builder => builder.AddProblemDetails(o => o.IncludeExceptionDetails = true)
        );

        var response = await server.Client.GetAsync("/boom", TestContext.Current.CancellationToken);
        var problem = await ReadProblemAsync(response);
        var exception = problem.GetProperty("exception");

        Assert.Equal("System.InvalidOperationException", exception.GetProperty("type").GetString());
        Assert.Equal("the real reason", exception.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Lets_a_specific_handler_go_first()
    {
        await using var server = await TestServer.StartAsync(
            app => app.MapGet("/boom", _ => throw new MissingThingException()),
            builder =>
            {
                builder.AddExceptionHandler(async (ctx, ex, ct) =>
                {
                    if (ex is not MissingThingException)
                        return false;

                    ctx.Response.StatusCode = 410;
                    await ctx.Response.WriteTextAsync("handled by the specific one", cancellationToken: ct);

                    return true;
                });

                builder.AddProblemDetails();
            }
        );

        var response = await server.Client.GetAsync("/boom", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal(
            "handled by the specific one",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
    }

    /// <summary>
    /// Routing's 404 never throws, so without the middleware a client gets JSON for one failure and
    /// an empty body for the next.
    /// </summary>
    [Fact]
    public async Task Gives_a_bodiless_404_a_problem_body()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseProblemDetails();
                app.MapGet("/known", ctx => ctx.Response.WriteTextAsync("here"));
            },
            builder => builder.AddProblemDetails()
        );

        var response = await server.Client.GetAsync("/unknown", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await ReadProblemAsync(response);

        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.Equal("/unknown", problem.GetProperty("instance").GetString());
    }

    /// <summary>A handler that wrote its own error said what it meant.</summary>
    [Fact]
    public async Task Leaves_an_error_that_already_has_a_body_alone()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseProblemDetails();
                app.MapGet("/nope", async ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    await ctx.Response.WriteTextAsync("my own words");
                });
            },
            builder => builder.AddProblemDetails()
        );

        var response = await server.Client.GetAsync("/nope", TestContext.Current.CancellationToken);

        Assert.Equal("my own words", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Leaves_successful_responses_alone()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseProblemDetails();
                app.MapGet("/fine", ctx => ctx.Response.WriteTextAsync("ok"));
            },
            builder => builder.AddProblemDetails()
        );

        var response = await server.Client.GetAsync("/fine", TestContext.Current.CancellationToken);

        Assert.Equal("ok", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Runs_the_customize_callback()
    {
        await using var server = await TestServer.StartAsync(
            app => app.MapGet("/boom", _ => throw new InvalidOperationException("nope")),
            builder => builder.AddProblemDetails(o =>
                o.Customize = ctx => ctx.ProblemDetails.Extensions["supportId"] = "S-1234"
            )
        );

        var response = await server.Client.GetAsync("/boom", TestContext.Current.CancellationToken);

        Assert.Equal("S-1234", (await ReadProblemAsync(response)).GetProperty("supportId").GetString());
    }
}

sealed class MissingThingException() : Exception("the thing is missing");

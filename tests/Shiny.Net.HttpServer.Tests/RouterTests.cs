using Shiny.Net.HttpServer.Routing;

namespace Shiny.Net.HttpServer.Tests;

public class RouteTemplateTests
{
    [Theory]
    [InlineData("/users", 1)]
    [InlineData("users", 1)]
    [InlineData("/users/{id}", 2)]
    [InlineData("/users/{id:int}/orders", 3)]
    [InlineData("/", 0)]
    [InlineData("", 0)]
    public void Parses_segment_counts(string template, int expected)
        => Assert.Equal(expected, RouteTemplate.Parse(template).Segments.Count);

    [Fact]
    public void Parses_parameter_metadata()
    {
        var template = RouteTemplate.Parse("/users/{id:int}/files/{*path}");

        Assert.Equal(RouteSegmentKind.Literal, template.Segments[0].Kind);
        Assert.Equal(RouteSegmentKind.Parameter, template.Segments[1].Kind);
        Assert.Equal("id", template.Segments[1].Text);
        Assert.Equal("int", template.Segments[1].Constraint.ToString());
        Assert.Equal(RouteSegmentKind.CatchAll, template.Segments[3].Kind);
        Assert.Equal("path", template.Segments[3].Text);
    }

    [Fact]
    public void Marks_trailing_optional_parameter()
        => Assert.True(RouteTemplate.Parse("/users/{id?}").Segments[1].IsOptional);

    [Theory]
    [InlineData("/users//orders")]
    [InlineData("/users/{id")]
    [InlineData("/users/{}")]
    [InlineData("/users/{id:nonsense}")]
    [InlineData("/files/{*path}/more")]
    [InlineData("/users/{id?}/orders")]
    [InlineData("/v{version}/users")]
    [InlineData("/files/{*path?}")]
    public void Rejects_malformed_templates(string template)
        => Assert.Throws<RouteTemplateException>(() => RouteTemplate.Parse(template));
}

public class RouteConstraintTests
{
    [Theory]
    [InlineData("int", "42", true)]
    [InlineData("int", "-1", true)]
    [InlineData("int", "4.2", false)]
    [InlineData("int", "abc", false)]
    [InlineData("long", "9999999999", true)]
    [InlineData("guid", "d0f9b9c8-0000-4000-8000-000000000000", true)]
    [InlineData("guid", "nope", false)]
    [InlineData("bool", "true", true)]
    [InlineData("bool", "1", false)]
    [InlineData("double", "1.5", true)]
    [InlineData("decimal", "1.5", true)]
    [InlineData("alpha", "abcDEF", true)]
    [InlineData("alpha", "abc1", false)]
    [InlineData("alpha", "", false)]
    [InlineData("minlength(3)", "abc", true)]
    [InlineData("minlength(3)", "ab", false)]
    [InlineData("maxlength(3)", "abcd", false)]
    [InlineData("length(3)", "abc", true)]
    public void Matches_expected_values(string constraint, string value, bool expected)
    {
        var parsed = RouteConstraint.Parse(constraint);
        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed!.Matches(value));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("minlength(-1)")]
    [InlineData("minlength(x)")]
    [InlineData("minlength(3")]
    public void Rejects_unknown_constraints(string constraint)
        => Assert.Null(RouteConstraint.Parse(constraint));
}

public class RouterTests
{
    static RouteEndpoint Endpoint(string method, string template)
        => new(_ => ValueTask.CompletedTask, method, RouteTemplate.Parse(template));

    static Router Build(params (string Method, string Template)[] routes)
    {
        var router = new Router();
        foreach (var (method, template) in routes)
            router.Add(Endpoint(method, template));

        return router;
    }

    [Fact]
    public void Matches_a_literal_route()
    {
        var router = Build(("GET", "/ping"));
        var match = router.Match("GET", "/ping", new RouteValueDictionary());

        Assert.True(match.IsMatch);
        Assert.Equal("GET /ping", match.Endpoint!.DisplayName);
    }

    [Fact]
    public void Captures_route_parameters()
    {
        var router = Build(("GET", "/users/{id}/orders/{orderId}"));
        var values = new RouteValueDictionary();

        Assert.True(router.Match("GET", "/users/7/orders/abc", values).IsMatch);
        Assert.Equal("7", values["id"]);
        Assert.Equal("abc", values["orderId"]);
    }

    [Fact]
    public void Prefers_a_literal_over_a_parameter()
    {
        var router = Build(("GET", "/users/{id}"), ("GET", "/users/me"));

        Assert.Equal("GET /users/me", router.Match("GET", "/users/me", new RouteValueDictionary()).Endpoint!.DisplayName);
        Assert.Equal("GET /users/{id}", router.Match("GET", "/users/9", new RouteValueDictionary()).Endpoint!.DisplayName);
    }

    [Fact]
    public void Prefers_a_constrained_parameter_regardless_of_registration_order()
    {
        // Registered least-specific first on purpose: precedence must come from the template, not
        // from the order someone happened to call Map in.
        var router = Build(("GET", "/things/{slug}"), ("GET", "/things/{id:int}"));

        Assert.Equal("GET /things/{id:int}", router.Match("GET", "/things/42", new RouteValueDictionary()).Endpoint!.DisplayName);
        Assert.Equal("GET /things/{slug}", router.Match("GET", "/things/blue", new RouteValueDictionary()).Endpoint!.DisplayName);
    }

    [Fact]
    public void Uses_a_catch_all_only_as_a_last_resort()
    {
        var router = Build(("GET", "/files/{*path}"), ("GET", "/files/readme"));
        var values = new RouteValueDictionary();

        Assert.Equal("GET /files/readme", router.Match("GET", "/files/readme", values).Endpoint!.DisplayName);

        values.Reset();
        Assert.True(router.Match("GET", "/files/a/b/c.txt", values).IsMatch);
        Assert.Equal("a/b/c.txt", values["path"]);
    }

    [Fact]
    public void Matches_an_optional_parameter_when_absent()
    {
        var router = Build(("GET", "/users/{id?}"));

        Assert.True(router.Match("GET", "/users", new RouteValueDictionary()).IsMatch);
        Assert.True(router.Match("GET", "/users/3", new RouteValueDictionary()).IsMatch);
    }

    [Fact]
    public void Treats_a_trailing_slash_as_the_same_route()
    {
        var router = Build(("GET", "/ping"));
        Assert.True(router.Match("GET", "/ping/", new RouteValueDictionary()).IsMatch);
    }

    [Fact]
    public void Does_not_bind_an_empty_segment_to_a_parameter()
    {
        var router = Build(("GET", "/users/{id}/orders"));
        Assert.False(router.Match("GET", "/users//orders", new RouteValueDictionary()).IsMatch);
    }

    [Fact]
    public void Reports_405_with_the_allowed_methods()
    {
        var router = Build(("GET", "/things"), ("DELETE", "/things"));
        var match = router.Match("POST", "/things", new RouteValueDictionary());

        Assert.False(match.IsMatch);
        Assert.True(match.IsMethodNotAllowed);
        Assert.Contains("GET", match.AllowedMethods);
        Assert.Contains("DELETE", match.AllowedMethods);
    }

    [Fact]
    public void Reports_404_rather_than_405_for_an_unknown_path()
    {
        var match = Build(("GET", "/things")).Match("POST", "/nope", new RouteValueDictionary());

        Assert.False(match.IsMatch);
        Assert.False(match.IsMethodNotAllowed);
    }

    [Fact]
    public void Serves_HEAD_from_the_GET_endpoint()
    {
        var match = Build(("GET", "/ping")).Match("HEAD", "/ping", new RouteValueDictionary());

        Assert.True(match.IsMatch);
        Assert.Equal("GET /ping", match.Endpoint!.DisplayName);
    }

    [Fact]
    public void Clears_captured_values_when_nothing_matches()
    {
        var router = Build(("GET", "/users/{id}/orders"));
        var values = new RouteValueDictionary();

        Assert.False(router.Match("GET", "/users/7/invoices", values).IsMatch);
        Assert.Equal(0, values.Count);
    }

    [Fact]
    public void Rejects_a_duplicate_registration_at_startup()
    {
        var router = Build(("GET", "/ping"));
        Assert.Throws<InvalidOperationException>(() => router.Add(Endpoint("GET", "/ping")));
    }

    [Fact]
    public void Allows_the_same_path_under_different_methods()
    {
        var router = Build(("GET", "/things"), ("POST", "/things"));
        Assert.Equal(2, router.Endpoints.Count);
    }

    [Fact]
    public void Matches_case_insensitively_on_literals()
        => Assert.True(Build(("GET", "/Ping")).Match("GET", "/ping", new RouteValueDictionary()).IsMatch);
}

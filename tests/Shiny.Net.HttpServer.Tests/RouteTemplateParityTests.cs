using Shiny.Net.HttpServer.Routing;
using Shiny.Net.HttpServer.SourceGenerators;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The generator validates route templates at compile time and the router parses them at runtime,
/// and the two are separate implementations — the generator targets netstandard2.0 and cannot
/// reference the runtime library. Separate implementations of the same grammar drift. These tests
/// are what stops that: every template either works in both or is rejected by both.
/// </summary>
public class RouteTemplateParityTests
{
    [Theory]
    [InlineData("/users")]
    [InlineData("/users/{id}")]
    [InlineData("/users/{id:int}")]
    [InlineData("/users/{id:guid}/orders/{orderId:long}")]
    [InlineData("/things/{slug:minlength(3)}")]
    [InlineData("/things/{code:length(4)}")]
    [InlineData("/things/{id:short}")]
    [InlineData("/things/{id:byte}")]
    [InlineData("/things/{ratio:float}")]
    [InlineData("/logs/{on:datetime}")]
    [InlineData("/logs/{on:dateonly}")]
    [InlineData("/logs/{at:timeonly}")]
    [InlineData("/logs/{took:timespan}")]
    [InlineData("/pages/{page:min(1)}")]
    [InlineData("/pages/{page:max(100)}")]
    [InlineData("/pages/{page:range(1,100)}")]
    [InlineData("/pages/{offset:min(-10)}")]
    [InlineData("/files/{*path}")]
    [InlineData("/users/{id?}")]
    [InlineData("/")]
    [InlineData("")]
    public void Both_parsers_accept_the_same_valid_templates(string template)
    {
        var runtime = RouteTemplate.Parse(template);
        var compileTime = RouteTemplateInfo.TryParse(template, out var error);

        Assert.NotNull(compileTime);
        Assert.Equal(string.Empty, error);

        var runtimeParameters = runtime.Segments
            .Where(s => s.Kind != RouteSegmentKind.Literal)
            .Select(s => s.Text)
            .ToArray();

        Assert.Equal(runtimeParameters, compileTime!.ParameterNames);
    }

    [Theory]
    [InlineData("/users//orders")]
    [InlineData("/users/{id")]
    [InlineData("/users/{}")]
    [InlineData("/users/{id:nonsense}")]
    [InlineData("/files/{*path}/more")]
    [InlineData("/users/{id?}/orders")]
    [InlineData("/v{version}/users")]
    [InlineData("/files/{*path?}")]
    [InlineData("/things/{code:minlength(-1)}")]
    [InlineData("/things/{code:length(-2)}")]
    [InlineData("/pages/{page:range(100,1)}")]
    [InlineData("/pages/{page:range(1)}")]
    [InlineData("/pages/{page:range(1,x)}")]
    [InlineData("/pages/{page:min(x)}")]
    [InlineData("/things/{slug:regex(^a.*$)}")]
    public void Both_parsers_reject_the_same_invalid_templates(string template)
    {
        Assert.Throws<RouteTemplateException>(() => RouteTemplate.Parse(template));

        var compileTime = RouteTemplateInfo.TryParse(template, out var error);

        Assert.Null(compileTime);
        Assert.NotEqual(string.Empty, error);
    }

    [Theory]
    [InlineData("/api/users", "/{id}", "/api/users/{id}")]
    [InlineData("api/users/", "{id}", "/api/users/{id}")]
    [InlineData("/api/users", "", "/api/users")]
    [InlineData("", "/ping", "/ping")]
    [InlineData("", "", "/")]
    [InlineData("/", "/", "/")]
    public void Combines_a_class_prefix_and_a_method_template(string prefix, string template, string expected)
        => Assert.Equal(expected, RouteTemplateInfo.Combine(prefix, template));

    [Fact]
    public void A_combined_template_is_routable()
    {
        var combined = RouteTemplateInfo.Combine("/api/widgets", "/{id:int}");
        var router = new Router();
        router.Add(new RouteEndpoint(_ => ValueTask.CompletedTask, "GET", RouteTemplate.Parse(combined)));

        var values = new RouteValueDictionary();

        Assert.True(router.Match("GET", "/api/widgets/12", values).IsMatch);
        Assert.Equal("12", values["id"]);
    }

    [Fact]
    public void Rejects_a_duplicated_token_at_compile_time()
    {
        Assert.Null(RouteTemplateInfo.TryParse("/a/{id}/b/{id}", out var error));
        Assert.Contains("more than once", error);
    }
}

using System.Net;

namespace Shiny.Net.HttpServer.Tests;

// ---------------------------------------------------------------------------
// Constraints have three separate implementations that have to agree: the
// generator validates the template at compile time, the router matches the
// segment at runtime, and the binder turns it into the parameter's type. These
// go through all three over a real socket — the class below would not compile
// if the generator rejected a constraint, and would not answer if the router
// and binder disagreed about what it means.
// ---------------------------------------------------------------------------

[Route("/constraints")]
public class ConstrainedEndpoints
{
    [Get("/short/{value:short}")]
    public string Short(short value) => $"short:{value}";

    [Get("/byte/{value:byte}")]
    public string Byte(byte value) => $"byte:{value}";

    [Get("/long/{value:long}")]
    public string Long(long value) => $"long:{value}";

    [Get("/float/{value:float}")]
    public string Float(float value) => $"float:{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    [Get("/guid/{value:guid}")]
    public string Guid(Guid value) => $"guid:{value}";

    [Get("/datetime/{value:datetime}")]
    public string DateTime(DateTime value) => $"datetime:{value:yyyy-MM-ddTHH:mm:ss}";

    [Get("/dateonly/{value:dateonly}")]
    public string DateOnly(DateOnly value) => $"dateonly:{value:yyyy-MM-dd}";

    [Get("/timeonly/{value:timeonly}")]
    public string TimeOnly(TimeOnly value) => $"timeonly:{value:HH\\:mm}";

    [Get("/timespan/{value:timespan}")]
    public string TimeSpan(TimeSpan value) => $"timespan:{value}";

    [Get("/page/{value:range(1,100)}")]
    public string Page(int value) => $"page:{value}";

    [Get("/atleast/{value:min(10)}")]
    public string AtLeast(int value) => $"atleast:{value}";

    // The constraint filters the route; it does not decide the parameter's type. An int-shaped
    // segment handed to a long is exactly what this should do.
    [Get("/widen/{value:int}")]
    public string Widen(long value) => $"widen:{value}";
}

public class RouteConstraintEndpointTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static Task<TestServer> StartAsync() => TestServer.StartAsync(app => app.MapConstrainedEndpoints());

    [Theory]
    [InlineData("/constraints/short/32767", "short:32767")]
    [InlineData("/constraints/byte/255", "byte:255")]
    [InlineData("/constraints/long/9223372036854775807", "long:9223372036854775807")]
    [InlineData("/constraints/float/1.5", "float:1.5")]
    [InlineData("/constraints/guid/d0f9b9c8-0000-4000-8000-000000000000", "guid:d0f9b9c8-0000-4000-8000-000000000000")]
    [InlineData("/constraints/datetime/2026-08-11T14:30:00", "datetime:2026-08-11T14:30:00")]
    [InlineData("/constraints/dateonly/2026-08-11", "dateonly:2026-08-11")]
    [InlineData("/constraints/timeonly/14:30", "timeonly:14:30")]
    [InlineData("/constraints/timespan/1.02:03:04", "timespan:1.02:03:04")]
    [InlineData("/constraints/page/50", "page:50")]
    [InlineData("/constraints/atleast/10", "atleast:10")]
    [InlineData("/constraints/widen/42", "widen:42")]
    public async Task Routes_and_binds_a_constrained_segment(string path, string expected)
    {
        await using var server = await StartAsync();

        Assert.Equal(expected, await server.Client.GetStringAsync(path, Token));
    }

    /// <summary>
    /// A value the constraint refuses is a <b>404</b> — the route simply did not match. That is not
    /// the same as a 400, which is what a matched route with an unparseable value gives you.
    /// </summary>
    [Theory]
    [InlineData("/constraints/short/32768")]
    [InlineData("/constraints/byte/256")]
    [InlineData("/constraints/byte/-1")]
    [InlineData("/constraints/datetime/notadate")]
    [InlineData("/constraints/page/0")]
    [InlineData("/constraints/page/101")]
    [InlineData("/constraints/atleast/9")]
    public async Task Does_not_route_a_value_the_constraint_refuses(string path)
    {
        await using var server = await StartAsync();

        var response = await server.Client.GetAsync(path, Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

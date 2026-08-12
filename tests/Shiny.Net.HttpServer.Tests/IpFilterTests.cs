using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.Tests;

public class IpAddressRangeTests
{
    [Theory]
    [InlineData("10.0.0.0/8", "10.255.3.9", true)]
    [InlineData("10.0.0.0/8", "11.0.0.1", false)]
    [InlineData("192.168.1.0/24", "192.168.1.255", true)]
    [InlineData("192.168.1.0/24", "192.168.2.1", false)]
    [InlineData("127.0.0.1", "127.0.0.1", true)]
    [InlineData("127.0.0.1", "127.0.0.2", false)]
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]
    [InlineData("2001:db8::/32", "2001:db8:1234::1", true)]
    [InlineData("2001:db8::/32", "2001:db9::1", false)]
    [InlineData("::1/128", "::1", true)]
    public void Matches_what_it_covers(string range, string address, bool expected)
        => Assert.Equal(expected, IpAddressRange.Parse(range).Contains(IPAddress.Parse(address)));

    [Fact]
    public void Masks_host_bits_rather_than_refusing_the_range()
    {
        // 10.1.2.3/8 is what people type. System.Net.IPNetwork throws on it; here the host bits are
        // simply dropped, because the author plainly meant 10.0.0.0/8.
        var range = IpAddressRange.Parse("10.1.2.3/8");

        Assert.Equal("10.0.0.0/8", range.ToString());
        Assert.True(range.Contains(IPAddress.Parse("10.9.9.9")));
    }

    [Fact]
    public void Unmaps_an_ipv4_mapped_ipv6_address()
    {
        // A client on a dual-stack listener arrives as ::ffff:127.0.0.1, and a rule written for
        // IPv4 has to match it or every dual-stack deployment silently blocks everyone.
        var range = IpAddressRange.Parse("127.0.0.0/8");

        Assert.True(range.Contains(IPAddress.Parse("::ffff:127.0.0.1")));
    }

    [Fact]
    public void Ranges_of_different_families_never_match()
        => Assert.False(IpAddressRange.Parse("0.0.0.0/0").Contains(IPAddress.Parse("2001:db8::1")));

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("10.0.0.0/33")]
    [InlineData("::1/129")]
    [InlineData("10.0.0.0/abc")]
    public void Rejects_nonsense(string value)
    {
        Assert.False(IpAddressRange.TryParse(value, out _));
        Assert.Throws<FormatException>(() => IpAddressRange.Parse(value));
    }
}

public class IpFilterPolicyTests
{
    [Fact]
    public void Only_deny_entries_makes_a_blacklist()
    {
        var policy = IpFilterPolicy.Create(p => p.Deny("10.0.0.0/8"));

        Assert.False(policy.IsWhitelist);
        Assert.False(policy.IsAllowed(IPAddress.Parse("10.1.1.1")));
        Assert.True(policy.IsAllowed(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void One_allow_entry_makes_a_whitelist()
    {
        var policy = IpFilterPolicy.Create(p => p.Allow("10.0.0.0/8"));

        Assert.True(policy.IsWhitelist);
        Assert.True(policy.IsAllowed(IPAddress.Parse("10.1.1.1")));
        Assert.False(policy.IsAllowed(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void A_denial_beats_a_wider_allow()
    {
        var policy = IpFilterPolicy.Create(p => p.Allow("10.0.0.0/8").Deny("10.4.0.0/16"));

        Assert.True(policy.IsAllowed(IPAddress.Parse("10.1.0.1")));
        Assert.False(policy.IsAllowed(IPAddress.Parse("10.4.0.1")));
    }

    [Fact]
    public void An_unknown_address_fails_closed()
    {
        // A filter that cannot see the caller has established nothing about them.
        Assert.False(IpFilterPolicy.Create(p => p.Allow("10.0.0.0/8")).IsAllowed(null));
        Assert.True(IpFilterPolicy.Create(p => p.Allow("10.0.0.0/8").AllowUnknownAddress()).IsAllowed(null));
    }

    [Fact]
    public void Loopback_and_private_shorthands_cover_what_they_say()
    {
        var policy = IpFilterPolicy.Create(p => p.AllowLoopback().AllowPrivateNetworks());

        Assert.True(policy.IsAllowed(IPAddress.Loopback));
        Assert.True(policy.IsAllowed(IPAddress.IPv6Loopback));
        Assert.True(policy.IsAllowed(IPAddress.Parse("192.168.0.10")));
        Assert.True(policy.IsAllowed(IPAddress.Parse("172.16.5.5")));
        Assert.True(policy.IsAllowed(IPAddress.Parse("10.9.9.9")));
        Assert.False(policy.IsAllowed(IPAddress.Parse("8.8.8.8")));
        Assert.False(policy.IsAllowed(IPAddress.Parse("172.32.0.1")));
    }
}

public class IpFilterMiddlewareTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lets_a_listed_caller_through()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.UseIpFilter(p => p.AllowLoopback());
            app.MapGet("/x", ctx => ctx.Response.WriteAsync("ok"));
        });

        Assert.Equal("ok", await server.Client.GetStringAsync("/x", Token));
    }

    [Fact]
    public async Task Turns_away_a_caller_outside_the_whitelist()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.UseIpFilter(p => p.Allow("10.0.0.0/8"));
            app.MapGet("/x", ctx => ctx.Response.WriteAsync("ok"));
        });

        Assert.Equal(HttpStatusCode.Forbidden, (await server.Client.GetAsync("/x", Token)).StatusCode);
    }

    [Fact]
    public async Task A_denial_beats_the_allow_it_sits_inside()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.UseIpFilter(p => p.AllowLoopback().Deny("127.0.0.1/32"));
            app.MapGet("/x", ctx => ctx.Response.WriteAsync("ok"));
        });

        Assert.Equal(HttpStatusCode.Forbidden, (await server.Client.GetAsync("/x", Token)).StatusCode);
    }

    [Fact]
    public async Task Blocks_before_routing_so_a_stranger_cannot_even_map_the_server()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.UseIpFilter(p => p.Allow("10.0.0.0/8"));
            app.MapGet("/x", ctx => ctx.Response.WriteAsync("ok"));
        });

        // 403 rather than 404: which paths exist is itself worth not telling them.
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Client.GetAsync("/nowhere", Token)).StatusCode);
    }

    [Fact]
    public async Task The_handler_never_runs_for_a_blocked_caller()
    {
        var ran = false;

        await using var server = await TestServer.StartAsync(app =>
        {
            app.UseIpFilter(p => p.Allow("10.0.0.0/8"));
            app.MapGet("/x", ctx => { ran = true; return ctx.Response.WriteAsync("ok"); });
        });

        await server.Client.GetAsync("/x", Token);

        Assert.False(ran);
    }

    [Fact]
    public async Task An_endpoint_can_name_a_tighter_policy()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseIpFilter();
                app.MapGet("/open", ctx => ctx.Response.WriteAsync("open"));
                app.MapGet("/admin", ctx => ctx.Response.WriteAsync("admin")).RequireIpFilter("admin");
            },
            builder => builder.Services.AddIpFilter(o =>
            {
                o.SetDefaultPolicy(p => p.AllowLoopback());
                o.AddPolicy("admin", p => p.Allow("10.0.0.0/8"));
            })
        );

        Assert.Equal("open", await server.Client.GetStringAsync("/open", Token));
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Client.GetAsync("/admin", Token)).StatusCode);
    }

    [Fact]
    public async Task An_endpoint_can_opt_out_of_the_default_policy()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseIpFilter();
                app.MapGet("/x", ctx => ctx.Response.WriteAsync("blocked"));
                app.MapGet("/health", ctx => ctx.Response.WriteAsync("ok")).AllowAnyIp();
            },
            builder => builder.Services.AddIpFilter(o => o.SetDefaultPolicy(p => p.Allow("10.0.0.0/8")))
        );

        Assert.Equal(HttpStatusCode.Forbidden, (await server.Client.GetAsync("/x", Token)).StatusCode);
        Assert.Equal("ok", await server.Client.GetStringAsync("/health", Token));
    }

    [Fact]
    public async Task The_rejection_response_can_be_replaced()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseIpFilter();
                app.MapGet("/x", ctx => ctx.Response.WriteAsync("ok"));
            },
            builder => builder.Services.AddIpFilter(o =>
            {
                o.SetDefaultPolicy(p => p.Allow("10.0.0.0/8"));
                o.OnRejected = (ctx, address) =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return ctx.Response.WriteAsync($"nothing here for {address}");
                };
            })
        );

        var response = await server.Client.GetAsync("/x", Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("127.0.0.1", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task A_named_policy_that_was_never_registered_says_so()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseIpFilter();
                app.MapGet("/x", ctx => ctx.Response.WriteAsync("ok")).RequireIpFilter("missing");
            },
            builder => builder.Services.AddIpFilter(o => o.SetDefaultPolicy(p => p.AllowLoopback()))
        );

        var response = await server.Client.GetAsync("/x", Token);
        var body = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("missing", body);
    }

    [Fact]
    public async Task UseIpFilter_without_a_policy_says_so_rather_than_failing_open()
    {
        await using var server = await TestServer.StartAsync(app => app.MapGet("/x", ctx => ctx.Response.WriteAsync("ok")));

        var error = Assert.Throws<InvalidOperationException>(() => server.Server.UseIpFilter());
        Assert.Contains("AddIpFilter", error.Message);
    }
}

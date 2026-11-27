using System.Text;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>Tests that measure the whole process, so nothing else may run while they do.</summary>
[CollectionDefinition(nameof(ProcessWideMeasurements), DisableParallelization = true)]
public sealed class ProcessWideMeasurements;

/// <summary>
/// <see cref="Http2HardeningTests"/> cases that measure allocation with <see cref="GC.GetTotalAllocatedBytes(bool)"/>,
/// which counts every thread in the process: a test moving megabytes alongside would be counted against the server.
/// </summary>
[Collection(nameof(ProcessWideMeasurements))]
public class Http2AllocationTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    static void MapSize(HttpServer app) => app.MapGet("/size/{n}", async ctx =>
    {
        var n = int.Parse((string)ctx.Request.RouteValues["n"]!);
        await ctx.Response.WriteBytesAsync(new byte[n], cancellationToken: ctx.RequestAborted);
    });

    [Fact]
    public async Task Refuses_a_header_block_that_decodes_past_the_list_limit()
    {
        // 16 KB on the wire: one 4 KB cookie added to the dynamic table, then thousands of one-byte
        // references to it - 16 MB of cookies once decoded.
        var invoked = 0;

        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapGet("/cookies", ctx =>
            {
                Interlocked.Increment(ref invoked);
                return ctx.Response.WriteAsync("ok");
            });

            MapSize(app);
        });

        await using var client = await Http2RawClient.ConnectAsync(server.Port);

        var block = Http2RawClient.RequestBlock("GET", "/cookies");
        Http2RawClient.WriteInteger(block, 32, 6, 0x40); // literal, indexed, name "cookie"
        Http2RawClient.WriteString(block, new string('a', 4000));

        while (block.Count < 16_000)
            block.Add(0x80 | 62); // the dynamic entry just added

        var allocated = GC.GetTotalAllocatedBytes(true);
        await client.SendFrameAsync(Http2RawFrame.Headers, 0x5, 1, block.ToArray());

        var response = await client.WaitForAsync(x => x.Type == Http2RawFrame.Headers && x.StreamId == 1, Wait);
        allocated = GC.GetTotalAllocatedBytes(true) - allocated;

        Assert.NotNull(response);
        Assert.Contains("431", Encoding.ASCII.GetString(response.Payload));
        Assert.Equal(0, Volatile.Read(ref invoked));
        Assert.True(allocated < 64L * 1024 * 1024, $"Allocated {allocated / (1024 * 1024)} MiB refusing the request.");

        // Refused, not disconnected: the block was decoded in full, so HPACK is still in step.
        await client.HeadersAsync(3, "GET", "/size/5", endStream: true);
        Assert.NotNull(await client.WaitForEndAsync(3, Wait));
    }
}

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// For server-side state that settles after the client already has its response.
/// <para>
/// A response with a known length is finished the moment the handler writes it, so anything that
/// runs as the pipeline unwinds - middleware after <c>next</c>, scope disposal, telemetry - can land
/// after the client's request has returned. Asserting straight away is a race a fast machine usually
/// wins and a CI runner loses.
/// </para>
/// </summary>
static class Wait
{
    /// <summary>Polls <paramref name="condition"/> until it holds or five seconds pass; the assertion after it reports which.</summary>
    public static async Task UntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10, cancellationToken);
    }
}

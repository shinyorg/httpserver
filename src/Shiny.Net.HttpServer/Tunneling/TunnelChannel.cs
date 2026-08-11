using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace Shiny.Net.HttpServer.Tunneling;

/// <summary>
/// Handles one frame. <paramref name="payload"/> points into the read buffer and is valid only
/// until the returned task completes.
/// </summary>
public delegate ValueTask TunnelFrameHandler(
    TunnelFrameType type,
    uint streamId,
    ReadOnlySequence<byte> payload,
    CancellationToken cancellationToken
);

/// <summary>
/// The framed conversation over a tunnel's single duplex byte stream. Both ends — the client
/// dialling out and the relay accepting — use this, so the two can never disagree about framing.
/// <para>
/// Writes are serialized: many streams share one connection and a half-written frame from one of
/// them would corrupt every other.
/// </para>
/// </summary>
public sealed class TunnelChannel(PipeReader input, PipeWriter output) : IAsyncDisposable
{
    readonly SemaphoreSlim writeGate = new(1, 1);
    int disposed;

    public async ValueTask SendAsync(
        TunnelFrameType type,
        uint streamId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default
    )
    {
        await this.writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TunnelProtocol.Write(output, type, streamId, payload.Span);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.writeGate.Release();
        }
    }

    /// <summary>Sends a frame straight from a pipe buffer, with no intermediate copy.</summary>
    public async ValueTask SendAsync(
        TunnelFrameType type,
        uint streamId,
        ReadOnlySequence<byte> payload,
        CancellationToken cancellationToken = default
    )
    {
        await this.writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TunnelProtocol.Write(output, type, streamId, in payload);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.writeGate.Release();
        }
    }

    public ValueTask SendAsync(
        TunnelFrameType type,
        uint streamId,
        string payload,
        CancellationToken cancellationToken = default
    ) => this.SendAsync(type, streamId, Encoding.UTF8.GetBytes(payload), cancellationToken);

    public ValueTask SendAsync(TunnelFrameType type, uint streamId = 0, CancellationToken cancellationToken = default)
        => this.SendAsync(type, streamId, ReadOnlyMemory<byte>.Empty, cancellationToken);

    /// <summary>
    /// Reads frames until the peer closes or <paramref name="cancellationToken"/> fires, handing
    /// each to <paramref name="handler"/>.
    /// </summary>
    public async Task RunAsync(TunnelFrameHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);

        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            try
            {
                while (TunnelProtocol.TryRead(ref buffer, out var type, out var streamId, out var payload))
                    await handler(type, streamId, payload, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Everything TryRead consumed is gone; the remainder has been examined, so the next
                // read waits for genuinely new bytes instead of spinning on a partial frame.
                input.AdvanceTo(buffer.Start, buffer.End);
            }

            if (result.IsCompleted || result.IsCanceled)
                return;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        try
        {
            await input.CompleteAsync().ConfigureAwait(false);
            await output.CompleteAsync().ConfigureAwait(false);
        }
        catch
        {
            // Completing pipes over an already-dead transport is expected to fail.
        }

        this.writeGate.Dispose();
    }
}

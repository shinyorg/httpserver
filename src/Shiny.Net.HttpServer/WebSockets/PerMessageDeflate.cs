using System.Buffers;
using System.IO.Compression;

namespace Shiny.Net.HttpServer.WebSockets;

/// <summary>
/// permessage-deflate (RFC 7692), in the one shape that is worth having here: no context takeover
/// in either direction.
/// <para>
/// With context takeover, both peers keep one deflate stream alive across every message, so each
/// message compresses against the history of the ones before it. It compresses better — and it
/// requires a persistent zlib window per connection in each direction, which is 300KB or so of
/// memory per socket held for as long as the socket lives. On a phone serving a dozen clients that
/// is the whole budget, spent on the least valuable thing in the room.
/// </para>
/// <para>
/// So the server always answers with <c>server_no_context_takeover; client_no_context_takeover</c>,
/// which RFC 7692 §7.1.1.1 lets it impose, and every message is compressed on its own. A JSON
/// payload still compresses to a fraction of its size; nothing has to remember anything.
/// </para>
/// </summary>
static class PerMessageDeflate
{
    public const string ExtensionName = "permessage-deflate";

    /// <summary>
    /// The four bytes RFC 7692 §7.2.1 says to strip from a compressed message and put back before
    /// inflating it. They are an empty non-compressed deflate block — the sync flush marker — and
    /// they are identical on every message, so they are not worth sending.
    /// </summary>
    static ReadOnlySpan<byte> SyncTail => [0x00, 0x00, 0xFF, 0xFF];

    /// <summary>
    /// Whether the client offered the extension, and what to answer with.
    /// <para>
    /// The offer is a list of alternatives; the first one that is acceptable wins. Anything with a
    /// parameter this server does not understand is skipped rather than rejected, which is what the
    /// spec asks for — a client offering a window size and a fallback gets the fallback.
    /// </para>
    /// </summary>
    public static string? Negotiate(string? offered)
    {
        if (offered is not { Length: > 0 })
            return null;

        foreach (var candidate in offered.Split(','))
        {
            var parameters = candidate.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (parameters.Length == 0 || !parameters[0].Equals(ExtensionName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!ParametersAreAcceptable(parameters))
                continue;

            // Both no-context-takeover flags, always. The client is required to honour them.
            return $"{ExtensionName}; server_no_context_takeover; client_no_context_takeover";
        }

        return null;
    }

    static bool ParametersAreAcceptable(string[] parameters)
    {
        for (var i = 1; i < parameters.Length; i++)
        {
            var name = parameters[i].Split('=', 2)[0].Trim();

            // A window-bits hint is a request for a smaller window than the default. Since every
            // message is independent here, a smaller window costs a little compression and nothing
            // else, so both forms are accepted and neither changes what the server does.
            if (name is "server_no_context_takeover"
                or "client_no_context_takeover"
                or "server_max_window_bits"
                or "client_max_window_bits")
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>Deflates a message payload, without the sync tail.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> payload)
    {
        var output = new MemoryStream(payload.Length);
        var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true);

        deflate.Write(payload);

        // A sync flush, not a close. Closing terminates the stream with a final block, and a
        // receiver that appends the sync tail to that gets "data after BFINAL" and drops the
        // connection. Flushing ends the output on 00 00 FF FF, which is precisely the four bytes
        // RFC 7692 says to remove.
        deflate.Flush();

        var length = (int)output.Length;

        // Disposing appends the terminator, which is why the length is taken first — everything
        // past it is ignored.
        deflate.Dispose();

        var compressed = output.GetBuffer().AsSpan(0, length);

        return compressed.EndsWith(SyncTail) ? compressed[..^SyncTail.Length].ToArray() : compressed.ToArray();
    }

    /// <summary>Inflates a message payload, putting the sync tail back first.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> payload, long maxLength)
    {
        var input = new MemoryStream(payload.Length + SyncTail.Length);
        input.Write(payload);
        input.Write(SyncTail);
        input.Position = 0;

        using var inflate = new DeflateStream(input, CompressionMode.Decompress);
        var output = new ArrayBufferWriter<byte>(Math.Max(payload.Length * 2, 256));

        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var read = inflate.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;

                // The whole point of a limit on a decompressed message: the compressed frame was
                // already inside the frame limit, and that says nothing about what it expands to.
                if (output.WrittenCount + read > maxLength)
                    throw new WebSocketProtocolException(WebSocketCloseStatus.MessageTooBig, "The decompressed message is too large.");

                output.Write(buffer.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return output.WrittenSpan.ToArray();
    }
}

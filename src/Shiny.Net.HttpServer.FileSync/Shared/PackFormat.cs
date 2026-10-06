using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

#if FILESYNC_CLIENT
namespace Shiny.Net.HttpServer.FileSync.Client.Internal;
#else
namespace Shiny.Net.HttpServer.FileSync.Internal;
#endif

/// <summary>
/// A pack: several chunks in one body, so moving forty changed chunks is one transfer and not forty.
/// <code>
/// "SFP1"
/// repeated: [32-byte SHA-256][4-byte big-endian length][length bytes]
/// </code>
/// Every chunk carries its own hash, and the reader checks it, so a pack is self-verifying - a
/// corrupted or truncated one is refused instead of being stored under a hash it does not have.
/// </summary>
static class PackFormat
{
    public const string ContentType = "application/vnd.shiny.filesync-pack";
    public const int HashLength = 32;

    static ReadOnlySpan<byte> Magic => "SFP1"u8;

    public static async ValueTask WriteHeaderAsync(Stream destination, CancellationToken cancellationToken)
        => await destination.WriteAsync(Magic.ToArray(), cancellationToken).ConfigureAwait(false);

    public static async ValueTask WriteChunkAsync(Stream destination, string hash, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var header = new byte[HashLength + 4];
        Convert.FromHexString(hash).CopyTo(header, 0);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(HashLength), data.Length);

        await destination.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await destination.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads every chunk, verifying each, and hands it to <paramref name="onChunk"/>. The buffer is
    /// rented and only valid for the duration of the callback. Throws <see cref="InvalidDataException"/>
    /// on a malformed pack, a chunk above <paramref name="maxChunkSize"/>, or a hash that does not match.
    /// </summary>
    public static async ValueTask<int> ReadAsync(
        Stream source,
        int maxChunkSize,
        Func<string, ReadOnlyMemory<byte>, ValueTask> onChunk,
        CancellationToken cancellationToken
    )
    {
        var header = new byte[HashLength + 4];

        if (!await FillAsync(source, header.AsMemory(0, 4), cancellationToken).ConfigureAwait(false) || !header.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("Not a pack.");

        var count = 0;

        while (true)
        {
            var first = await source.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);

            if (first == 0)
                return count;

            if (!await FillAsync(source, header.AsMemory(1), cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("The pack ends inside a chunk header.");

            var hash = Convert.ToHexStringLower(header.AsSpan(0, HashLength));
            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(HashLength));

            if (length < 0 || length > maxChunkSize)
                throw new InvalidDataException($"Chunk {hash} is {length} bytes; the limit is {maxChunkSize}.");

            var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));

            try
            {
                var data = buffer.AsMemory(0, length);

                if (!await FillAsync(source, data, cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException($"The pack ends inside chunk {hash}.");

                if (!Convert.ToHexStringLower(SHA256.HashData(data.Span)).Equals(hash, StringComparison.Ordinal))
                    throw new InvalidDataException($"Chunk {hash} does not match its hash.");

                await onChunk(hash, data).ConfigureAwait(false);
                count++;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    static async ValueTask<bool> FillAsync(Stream source, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await source.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);

            if (read == 0)
                return false;

            total += read;
        }

        return true;
    }
}

/// <summary>Chunk hashes: lower-case hex SHA-256, the only form either end accepts.</summary>
static class ChunkHash
{
    public static bool IsValid(string? hash)
    {
        if (hash is null || hash.Length != PackFormat.HashLength * 2)
            return false;

        foreach (var c in hash)
        {
            if (!char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))
                return false;
        }

        return true;
    }
}

/// <summary>
/// The one shape a synced path may take: relative, <c>/</c>-separated, no empty, <c>.</c> or
/// <c>..</c> segments, nothing a file system on one of the clients would refuse. Checked on the
/// server, which may build a file path out of it, and on the client, which certainly does.
/// </summary>
static class SyncPath
{
    public const int MaxLength = 1024;
    const int MaxSegment = 255;

    public static bool TryNormalize(string? path, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
            return false;

        var trimmed = path.Replace('\\', '/').Trim('/');

        if (trimmed.Length == 0 || trimmed.Length > MaxLength)
            return false;

        foreach (var segment in trimmed.Split('/'))
        {
            if (segment.Length == 0 || segment.Length > MaxSegment || segment is "." or "..")
                return false;

            // Trailing dots and spaces are silently dropped by Windows, so two different server
            // paths would land on one file there.
            if (segment[^1] is '.' or ' ')
                return false;

            foreach (var c in segment)
            {
                if (char.IsControl(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|')
                    return false;
            }
        }

        normalized = trimmed;
        return true;
    }
}

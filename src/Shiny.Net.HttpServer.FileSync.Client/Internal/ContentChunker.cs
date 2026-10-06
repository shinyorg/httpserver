using System.Numerics;
using System.Security.Cryptography;

namespace Shiny.Net.HttpServer.FileSync.Client.Internal;

/// <summary>One chunk of a local file: where it sits and what it hashes to.</summary>
readonly record struct LocalChunk(long Offset, int Length, string Hash);

/// <summary>A file split into chunks, with the whole-file hash computed on the same pass.</summary>
sealed record ChunkedFile(long Size, string Hash, DateTime ModifiedUtc, IReadOnlyList<LocalChunk> Chunks);

/// <summary>A file changed while it was being read; it is picked up again on the next pass.</summary>
sealed class FileChangedException(string path) : IOException($"'{path}' changed while it was being read.");

/// <summary>
/// Content-defined chunking - FastCDC (Xia et al., 2016/2020) with normalized chunking.
/// <para>
/// Fixed-size blocks fall apart on an insertion: one byte added near the start of a file moves
/// every block boundary after it, and every block looks new. Here a boundary is wherever a rolling
/// hash of the last few dozen bytes hits a pattern, so boundaries move with the content - an
/// insertion changes the chunk it lands in, maybe its neighbour, and nothing else. That is what
/// makes an edit to a large file cheap to sync.
/// </para>
/// <para>
/// The gear table is generated from a fixed seed, so every client cuts the same bytes the same
/// way and their chunks deduplicate against each other on the server.
/// </para>
/// </summary>
static class ContentChunker
{
    static readonly ulong[] Gear = BuildGear();

    static ulong[] BuildGear()
    {
        // SplitMix64 from a constant: deterministic, well-distributed, and identical everywhere.
        var gear = new ulong[256];
        var state = 0x5348494E5953594EUL; // "SHINYSYN"

        for (var i = 0; i < gear.Length; i++)
        {
            var z = state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            gear[i] = z ^ (z >> 31);
        }

        return gear;
    }

    public static async Task<ChunkedFile> ChunkFileAsync(string path, int min, int average, int max, CancellationToken cancellationToken)
    {
        // Read now: FileInfo loads lazily, and a snapshot taken after the read compares nothing.
        var before = new FileInfo(path);
        var beforeLength = before.Length;
        var beforeModified = before.LastWriteTimeUtc;
        var bits = BitOperations.Log2((uint)average);

        // Normalized chunking: a harder mask before the average size and an easier one after it
        // pulls chunk sizes in towards the average, which is most of FastCDC's dedup gain.
        var maskSmall = Mask(bits + 1);
        var maskLarge = Mask(bits - 1);

        var chunks = new List<LocalChunk>();
        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[max * 2];
        int start = 0, end = 0;
        long offset = 0;
        var eof = false;

        await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true))
        {
            while (true)
            {
                if (!eof && end - start < max)
                {
                    Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                    end -= start;
                    start = 0;

                    while (end < buffer.Length)
                    {
                        var read = await file.ReadAsync(buffer.AsMemory(end), cancellationToken).ConfigureAwait(false);

                        if (read == 0)
                        {
                            eof = true;
                            break;
                        }

                        end += read;
                    }
                }

                if (start == end)
                    break;

                var length = Cut(buffer.AsSpan(start, end - start), min, average, max, maskSmall, maskLarge);
                var span = buffer.AsSpan(start, length);

                whole.AppendData(span);
                chunks.Add(new LocalChunk(offset, length, Convert.ToHexStringLower(SHA256.HashData(span))));

                offset += length;
                start += length;
            }
        }

        var after = new FileInfo(path);

        if (after.Length != offset || beforeLength != offset || after.LastWriteTimeUtc != beforeModified)
            throw new FileChangedException(path);

        return new ChunkedFile(offset, Convert.ToHexStringLower(whole.GetHashAndReset()), after.LastWriteTimeUtc, chunks);
    }

    /// <summary>Where the next chunk ends, as a length from the start of <paramref name="data"/>.</summary>
    internal static int Cut(ReadOnlySpan<byte> data, int min, int average, int max, ulong maskSmall, ulong maskLarge)
    {
        var n = data.Length;

        if (n <= min)
            return n;

        if (n > max)
            n = max;

        var normal = Math.Min(average, n);
        ulong fingerprint = 0;
        var i = min;

        for (; i < normal; i++)
        {
            fingerprint = (fingerprint << 1) + Gear[data[i]];

            if ((fingerprint & maskSmall) == 0)
                return i;
        }

        for (; i < n; i++)
        {
            fingerprint = (fingerprint << 1) + Gear[data[i]];

            if ((fingerprint & maskLarge) == 0)
                return i;
        }

        return n;
    }

    // The high bits: after the shift-and-add, they are the ones every byte of the window reached.
    internal static ulong Mask(int bits) => bits <= 0 ? 0 : ~0UL << (64 - Math.Min(bits, 63));
}

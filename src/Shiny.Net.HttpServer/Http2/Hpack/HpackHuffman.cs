namespace Shiny.Net.HttpServer.Http2.Hpack;

/// <summary>
/// The HPACK Huffman code (RFC 7541 Appendix B).
/// <para>
/// A fixed, canonical code — not one negotiated per connection — so the table is a constant and the
/// decoding tree is built once. Every real client Huffman-encodes at least some header values, so a
/// decoder that skips this cannot talk to anything; the encoder here deliberately does not use it,
/// because emitting a raw literal is always legal and the saving is not worth the extra code on the
/// write side.
/// </para>
/// </summary>
static class HpackHuffman
{
    /// <summary>The symbol that means "end of string". Its presence in real data is a protocol error.</summary>
    public const int EndOfString = 256;

    // Codes are right-aligned in a uint; Lengths says how many bits of each are significant.
    static readonly uint[] Codes =
    [
        0x1ff8, 0x7fffd8, 0xfffffe2, 0xfffffe3, 0xfffffe4, 0xfffffe5, 0xfffffe6, 0xfffffe7,
        0xfffffe8, 0xffffea, 0x3ffffffc, 0xfffffe9, 0xfffffea, 0x3ffffffd, 0xfffffeb, 0xfffffec,
        0xfffffed, 0xfffffee, 0xfffffef, 0xffffff0, 0xffffff1, 0xffffff2, 0x3ffffffe, 0xffffff3,
        0xffffff4, 0xffffff5, 0xffffff6, 0xffffff7, 0xffffff8, 0xffffff9, 0xffffffa, 0xffffffb,
        0x14, 0x3f8, 0x3f9, 0xffa, 0x1ff9, 0x15, 0xf8, 0x7fa,
        0x3fa, 0x3fb, 0xf9, 0x7fb, 0xfa, 0x16, 0x17, 0x18,
        0x0, 0x1, 0x2, 0x19, 0x1a, 0x1b, 0x1c, 0x1d,
        0x1e, 0x1f, 0x5c, 0xfb, 0x7ffc, 0x20, 0xffb, 0x3fc,
        0x1ffa, 0x21, 0x5d, 0x5e, 0x5f, 0x60, 0x61, 0x62,
        0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a,
        0x6b, 0x6c, 0x6d, 0x6e, 0x6f, 0x70, 0x71, 0x72,
        0xfc, 0x73, 0xfd, 0x1ffb, 0x7fff0, 0x1ffc, 0x3ffc, 0x22,
        0x7ffd, 0x3, 0x23, 0x4, 0x24, 0x5, 0x25, 0x26,
        0x27, 0x6, 0x74, 0x75, 0x28, 0x29, 0x2a, 0x7,
        0x2b, 0x76, 0x2c, 0x8, 0x9, 0x2d, 0x77, 0x78,
        0x79, 0x7a, 0x7b, 0x7ffe, 0x7fc, 0x3ffd, 0x1ffd, 0xffffffc,
        0xfffe6, 0x3fffd2, 0xfffe7, 0xfffe8, 0x3fffd3, 0x3fffd4, 0x3fffd5, 0x7fffd9,
        0x3fffd6, 0x7fffda, 0x7fffdb, 0x7fffdc, 0x7fffdd, 0x7fffde, 0xffffeb, 0x7fffdf,
        0xffffec, 0xffffed, 0x3fffd7, 0x7fffe0, 0xffffee, 0x7fffe1, 0x7fffe2, 0x7fffe3,
        0x7fffe4, 0x1fffdc, 0x3fffd8, 0x7fffe5, 0x3fffd9, 0x7fffe6, 0x7fffe7, 0xffffef,
        0x3fffda, 0x1fffdd, 0xfffe9, 0x3fffdb, 0x3fffdc, 0x7fffe8, 0x7fffe9, 0x1fffde,
        0x7fffea, 0x3fffdd, 0x3fffde, 0xfffff0, 0x1fffdf, 0x3fffdf, 0x7fffeb, 0x7fffec,
        0x1fffe0, 0x1fffe1, 0x3fffe0, 0x1fffe2, 0x7fffed, 0x3fffe1, 0x7fffee, 0x7fffef,
        0xfffea, 0x3fffe2, 0x3fffe3, 0x3fffe4, 0x7ffff0, 0x3fffe5, 0x3fffe6, 0x7ffff1,
        0x3ffffe0, 0x3ffffe1, 0xfffeb, 0x7fff1, 0x3fffe7, 0x7ffff2, 0x3fffe8, 0x1ffffec,
        0x3ffffe2, 0x3ffffe3, 0x3ffffe4, 0x7ffffde, 0x7ffffdf, 0x3ffffe5, 0xfffff1, 0x1ffffed,
        0x7fff2, 0x1fffe3, 0x3ffffe6, 0x7ffffe0, 0x7ffffe1, 0x3ffffe7, 0x7ffffe2, 0xfffff2,
        0x1fffe4, 0x1fffe5, 0x3ffffe8, 0x3ffffe9, 0xffffffd, 0x7ffffe3, 0x7ffffe4, 0x7ffffe5,
        0xfffec, 0xfffff3, 0xfffed, 0x1fffe6, 0x3fffe9, 0x1fffe7, 0x1fffe8, 0x7ffff3,
        0x3fffea, 0x3fffeb, 0x1ffffee, 0x1ffffef, 0xfffff4, 0xfffff5, 0x3ffffea, 0x7ffff4,
        0x3ffffeb, 0x7ffffe6, 0x3ffffec, 0x3ffffed, 0x7ffffe7, 0x7ffffe8, 0x7ffffe9, 0x7ffffea,
        0x7ffffeb, 0xffffffe, 0x7ffffec, 0x7ffffed, 0x7ffffee, 0x7ffffef, 0x7fffff0, 0x3ffffee,
        0x3fffffff
    ];

    static readonly byte[] Lengths =
    [
        13, 23, 28, 28, 28, 28, 28, 28,
        28, 24, 30, 28, 28, 30, 28, 28,
        28, 28, 28, 28, 28, 28, 30, 28,
        28, 28, 28, 28, 28, 28, 28, 28,
        6, 10, 10, 12, 13, 6, 8, 11,
        10, 10, 8, 11, 8, 6, 6, 6,
        5, 5, 5, 6, 6, 6, 6, 6,
        6, 6, 7, 8, 15, 6, 12, 10,
        13, 6, 7, 7, 7, 7, 7, 7,
        7, 7, 7, 7, 7, 7, 7, 7,
        7, 7, 7, 7, 7, 7, 7, 7,
        8, 7, 8, 13, 19, 13, 14, 6,
        15, 5, 6, 5, 6, 5, 6, 6,
        6, 5, 7, 7, 6, 6, 6, 5,
        6, 7, 6, 5, 5, 6, 7, 7,
        7, 7, 7, 15, 11, 14, 13, 28,
        20, 22, 20, 20, 22, 22, 22, 23,
        22, 23, 23, 23, 23, 23, 24, 23,
        24, 24, 22, 23, 24, 23, 23, 23,
        23, 21, 22, 23, 22, 23, 23, 24,
        22, 21, 20, 22, 22, 23, 23, 21,
        23, 22, 22, 24, 21, 22, 23, 23,
        21, 21, 22, 21, 23, 22, 23, 23,
        20, 22, 22, 22, 23, 22, 22, 23,
        26, 26, 20, 19, 22, 23, 22, 25,
        26, 26, 26, 27, 27, 26, 24, 25,
        19, 21, 26, 27, 27, 26, 27, 24,
        21, 21, 26, 26, 28, 27, 27, 27,
        20, 24, 20, 21, 22, 21, 21, 23,
        22, 22, 25, 25, 24, 24, 26, 23,
        26, 27, 26, 26, 27, 27, 27, 27,
        27, 28, 27, 27, 27, 27, 27, 26,
        30
    ];

    // A decoding tree, flattened. Each node owns two consecutive slots (bit 0 and bit 1); a
    // non-negative value is the index of a child node, and a negative value is -(symbol + 1).
    // Walking bit by bit is slower than a multi-bit table but is a fraction of the code, and header
    // blocks are small.
    static readonly int[] Tree = BuildTree();

    const int NoChild = 0;

    static int[] BuildTree()
    {
        var tree = new List<int>(4096);
        tree.AddRange([NoChild, NoChild]);

        for (var symbol = 0; symbol < Codes.Length; symbol++)
        {
            var code = Codes[symbol];
            var length = Lengths[symbol];
            var node = 0;

            for (var bit = length - 1; bit >= 0; bit--)
            {
                var branch = (int)((code >> bit) & 1);
                var slot = node + branch;
                var isLast = bit == 0;

                if (isLast)
                {
                    if (tree[slot] != NoChild)
                        throw new InvalidOperationException(
                            $"The HPACK Huffman table is not a prefix code: symbol {symbol} collides."
                        );

                    tree[slot] = -(symbol + 1);
                    break;
                }

                if (tree[slot] == NoChild)
                {
                    tree[slot] = tree.Count;
                    tree.AddRange([NoChild, NoChild]);
                }
                else if (tree[slot] < 0)
                {
                    throw new InvalidOperationException(
                        $"The HPACK Huffman table is not a prefix code: symbol {symbol} extends a leaf."
                    );
                }

                node = tree[slot];
            }
        }

        return [.. tree];
    }

    /// <summary>The code length in bits for a symbol, including <see cref="EndOfString"/>.</summary>
    public static int GetCodeLength(int symbol) => Lengths[symbol];

    /// <summary>
    /// The largest number of bytes <paramref name="encodedLength"/> bytes could decode to. The
    /// shortest code is five bits, so eight encoded bytes can never become more than twelve.
    /// </summary>
    public static int GetMaxDecodedLength(int encodedLength) => (encodedLength * 8 / 5) + 1;

    /// <summary>
    /// Decodes into <paramref name="destination"/>, returning the number of bytes written.
    /// </summary>
    public static int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var node = 0;
        var written = 0;
        var bitsConsumedSincePad = 0;

        foreach (var b in source)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                var branch = (b >> bit) & 1;
                var slot = Tree[node + branch];

                if (slot == NoChild)
                    throw new HpackException("The Huffman-coded string contains an invalid code.");

                if (slot < 0)
                {
                    var symbol = -(slot + 1);

                    // EOS never appears in a real payload; encountering it means the peer is either
                    // broken or probing.
                    if (symbol == EndOfString)
                        throw new HpackException("The Huffman-coded string contains EOS.");

                    if (written == destination.Length)
                        throw new HpackException("The decoded string is longer than expected.");

                    destination[written++] = (byte)symbol;
                    node = 0;
                    bitsConsumedSincePad = 0;
                    continue;
                }

                node = slot;
                bitsConsumedSincePad++;
            }
        }

        // The tail must be the all-ones padding of the EOS prefix, and shorter than one symbol.
        if (node != 0 && (bitsConsumedSincePad > 7 || !IsAllOnesPath(node)))
            throw new HpackException("The Huffman-coded string has invalid padding.");

        return written;
    }

    /// <summary>
    /// True when every step from the root to <paramref name="node"/> took the 1 branch, which is
    /// what valid padding looks like.
    /// </summary>
    static bool IsAllOnesPath(int node)
    {
        // Walking down the all-ones path from the root must reach this node.
        var current = 0;

        while (current != node)
        {
            var next = Tree[current + 1];
            if (next <= 0)
                return false;

            current = next;
        }

        return true;
    }

    /// <summary>Total bits <paramref name="value"/> would occupy if Huffman-coded.</summary>
    public static int GetEncodedLength(ReadOnlySpan<byte> value)
    {
        var bits = 0;
        foreach (var b in value)
            bits += Lengths[b];

        return (bits + 7) / 8;
    }

    /// <summary>Huffman-encodes into <paramref name="destination"/>, returning bytes written.</summary>
    public static int Encode(ReadOnlySpan<byte> value, Span<byte> destination)
    {
        ulong buffer = 0;
        var bits = 0;
        var written = 0;

        foreach (var b in value)
        {
            buffer = (buffer << Lengths[b]) | Codes[b];
            bits += Lengths[b];

            while (bits >= 8)
            {
                bits -= 8;
                destination[written++] = (byte)(buffer >> bits);
            }
        }

        if (bits > 0)
        {
            // Padded with the EOS prefix — all ones — which is what a decoder expects to find.
            var pad = 8 - bits;
            destination[written++] = (byte)((buffer << pad) | ((1u << pad) - 1));
        }

        return written;
    }
}

/// <summary>Thrown when a header block cannot be decoded. Always a connection error.</summary>
public sealed class HpackException(string message) : Exception(message);

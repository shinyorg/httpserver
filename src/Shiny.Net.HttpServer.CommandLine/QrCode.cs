using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Shiny.Net.HttpServer.CommandLine;


/// <summary>
/// A QR code: byte mode, error correction level M, versions 1 through 10. That tops out at 213
/// bytes, which is far more URL than anyone will point a phone at, and stopping there keeps the
/// whole encoder to arithmetic - no package, no reflection, nothing for the trimmer to lose.
/// </summary>
sealed class QrCode
{
    /// <summary>The largest byte-mode payload version 10 holds at level M.</summary>
    public const int MaxBytes = 213;

    // Total codewords, error correction codewords per block, and block count for each version at
    // level M. Everything else about a version's layout falls out of these three numbers.
    static readonly (int Total, int EcPerBlock, int Blocks)[] Versions =
    [
        (26, 10, 1),
        (44, 16, 1),
        (70, 26, 1),
        (100, 18, 2),
        (134, 24, 2),
        (172, 16, 4),
        (196, 18, 4),
        (242, 22, 4),
        (292, 22, 5),
        (346, 26, 5)
    ];

    // Row/column centres of the alignment patterns, per version.
    static readonly int[][] Alignments =
    [
        [],
        [6, 18],
        [6, 22],
        [6, 26],
        [6, 30],
        [6, 34],
        [6, 22, 38],
        [6, 24, 42],
        [6, 26, 46],
        [6, 28, 50]
    ];

    // GF(256) under x^8 + x^4 + x^3 + x^2 + 1, the field the Reed-Solomon codewords live in.
    static readonly byte[] Exp = new byte[255];
    static readonly byte[] Log = new byte[256];

    readonly bool[] modules;
    readonly bool[] isFunction;


    static QrCode()
    {
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;

            x <<= 1;
            if (x > 0xFF)
                x ^= 0x11D;
        }
    }


    QrCode(int version)
    {
        this.Version = version;
        this.Size = version * 4 + 17;
        this.modules = new bool[this.Size * this.Size];
        this.isFunction = new bool[this.Size * this.Size];
    }


    public int Version { get; }

    /// <summary>Width and height in modules, without the quiet zone a reader needs around it.</summary>
    public int Size { get; }

    /// <summary>True where the module is dark.</summary>
    public bool this[int x, int y] => this.modules[y * this.Size + x];


    /// <summary>Encodes <paramref name="text"/>, or fails if it is longer than <see cref="MaxBytes"/>.</summary>
    public static bool TryEncode(string text, [NotNullWhen(true)] out QrCode? code)
    {
        code = null;

        var data = Encoding.UTF8.GetBytes(text);
        var version = ChooseVersion(data.Length);
        if (version == 0)
            return false;

        code = new QrCode(version);
        code.Draw(Interleave(version, Payload(version, data)));
        return true;
    }


    /// <summary>The smallest version that holds the data, or 0 if none of them do.</summary>
    static int ChooseVersion(int length)
    {
        for (var version = 1; version <= Versions.Length; version++)
        {
            // 4 bits of mode indicator, then a character count that widens at version 10
            var needed = 4 + (version <= 9 ? 8 : 16) + length * 8;

            if (needed <= DataCodewords(version) * 8)
                return version;
        }
        return 0;
    }


    static int DataCodewords(int version)
    {
        var (total, ecPerBlock, blocks) = Versions[version - 1];
        return total - ecPerBlock * blocks;
    }


    /// <summary>Mode, length, the bytes themselves, then the padding that fills the version out.</summary>
    static byte[] Payload(int version, byte[] data)
    {
        var buffer = new byte[DataCodewords(version)];
        var index = 0;

        void Write(int value, int length)
        {
            for (var i = length - 1; i >= 0; i--)
            {
                if (((value >> i) & 1) != 0)
                    buffer[index >> 3] |= (byte)(0x80 >> (index & 7));

                index++;
            }
        }

        Write(0b0100, 4);
        Write(data.Length, version <= 9 ? 8 : 16);

        foreach (var b in data)
            Write(b, 8);

        // terminator, then out to the byte boundary, then the two padding bytes in turn
        Write(0, Math.Min(4, buffer.Length * 8 - index));
        index = (index + 7) & ~7;

        for (var pad = 0xEC; index < buffer.Length * 8; pad ^= 0xEC ^ 0x11)
            Write(pad, 8);

        return buffer;
    }


    /// <summary>
    /// Splits the payload into its blocks, gives each one its error correction codewords, and reads
    /// the lot back out a column at a time - the order the code is written in.
    /// </summary>
    static byte[] Interleave(int version, byte[] payload)
    {
        var (total, ecPerBlock, blockCount) = Versions[version - 1];
        var generator = Generator(ecPerBlock);

        var shortLength = payload.Length / blockCount;
        var longBlocks = payload.Length % blockCount;

        var blocks = new byte[blockCount][];
        var ecc = new byte[blockCount][];
        var offset = 0;

        for (var i = 0; i < blockCount; i++)
        {
            // the longer blocks are the last ones, which is what puts their extra codeword at the end
            var length = shortLength + (i >= blockCount - longBlocks ? 1 : 0);

            blocks[i] = payload[offset..(offset + length)];
            ecc[i] = Remainder(blocks[i], generator);
            offset += length;
        }

        var result = new byte[total];
        var index = 0;

        for (var i = 0; i <= shortLength; i++)
        {
            foreach (var block in blocks)
            {
                if (i < block.Length)
                    result[index++] = block[i];
            }
        }

        for (var i = 0; i < ecPerBlock; i++)
        {
            foreach (var block in ecc)
                result[index++] = block[i];
        }
        return result;
    }


    /// <summary>The Reed-Solomon divisor: (x - a^0)(x - a^1)...(x - a^(degree-1)), high term dropped.</summary>
    static byte[] Generator(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;

        byte root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], root);

                if (j + 1 < degree)
                    result[j] ^= result[j + 1];
            }
            root = Multiply(root, 2);
        }
        return result;
    }


    static byte[] Remainder(byte[] data, byte[] generator)
    {
        var result = new byte[generator.Length];

        foreach (var b in data)
        {
            var factor = (byte)(b ^ result[0]);
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;

            for (var i = 0; i < result.Length; i++)
                result[i] ^= Multiply(generator[i], factor);
        }
        return result;
    }


    static byte Multiply(byte a, byte b)
        => a == 0 || b == 0 ? (byte)0 : Exp[(Log[a] + Log[b]) % 255];


    void Draw(byte[] codewords)
    {
        this.DrawFunctionPatterns();
        this.DrawCodewords(codewords);

        var mask = this.ChooseMask();
        this.ApplyMask(mask);
        this.DrawFormat(mask);
    }


    void DrawFunctionPatterns()
    {
        for (var i = 0; i < this.Size; i++)
        {
            this.SetFunction(6, i, i % 2 == 0);
            this.SetFunction(i, 6, i % 2 == 0);
        }

        this.DrawFinder(3, 3);
        this.DrawFinder(this.Size - 4, 3);
        this.DrawFinder(3, this.Size - 4);

        var centres = Alignments[this.Version - 1];
        for (var i = 0; i < centres.Length; i++)
        {
            for (var j = 0; j < centres.Length; j++)
            {
                // the three corners are already finder patterns
                var corner = (i == 0 && j == 0)
                    || (i == 0 && j == centres.Length - 1)
                    || (i == centres.Length - 1 && j == 0);

                if (!corner)
                    this.DrawAlignment(centres[i], centres[j]);
            }
        }

        // claims the format area so the codewords step over it; the real bits are written last
        this.DrawFormat(0);
        this.DrawVersion();
    }


    void DrawFinder(int x, int y)
    {
        for (var dy = -4; dy <= 4; dy++)
        {
            for (var dx = -4; dx <= 4; dx++)
            {
                // rings out from the centre: dark, light at 2, dark at 3, then the light separator
                var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                var xx = x + dx;
                var yy = y + dy;

                if (xx >= 0 && xx < this.Size && yy >= 0 && yy < this.Size)
                    this.SetFunction(xx, yy, distance != 2 && distance != 4);
            }
        }
    }


    void DrawAlignment(int x, int y)
    {
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
                this.SetFunction(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }
    }


    /// <summary>Both copies of the 15 bit format word: level M, the mask, and its BCH check bits.</summary>
    void DrawFormat(int mask)
    {
        var data = mask; // level M is 0b00, so the word is the mask on its own
        var remainder = data;

        for (var i = 0; i < 10; i++)
            remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);

        var bits = ((data << 10) | remainder) ^ 0x5412;

        for (var i = 0; i <= 5; i++)
            this.SetFunction(8, i, Bit(bits, i));

        this.SetFunction(8, 7, Bit(bits, 6));
        this.SetFunction(8, 8, Bit(bits, 7));
        this.SetFunction(7, 8, Bit(bits, 8));

        for (var i = 9; i < 15; i++)
            this.SetFunction(14 - i, 8, Bit(bits, i));

        for (var i = 0; i < 8; i++)
            this.SetFunction(this.Size - 1 - i, 8, Bit(bits, i));

        for (var i = 8; i < 15; i++)
            this.SetFunction(8, this.Size - 15 + i, Bit(bits, i));

        this.SetFunction(8, this.Size - 8, true); // the module that is always dark
    }


    /// <summary>Version 7 and up carry their version number in two corners.</summary>
    void DrawVersion()
    {
        if (this.Version < 7)
            return;

        var remainder = this.Version;
        for (var i = 0; i < 12; i++)
            remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);

        var bits = (this.Version << 12) | remainder;

        for (var i = 0; i < 18; i++)
        {
            var bit = Bit(bits, i);
            var a = this.Size - 11 + i % 3;
            var b = i / 3;

            this.SetFunction(a, b, bit);
            this.SetFunction(b, a, bit);
        }
    }


    /// <summary>
    /// Walks the two-module-wide columns from the bottom right, alternating direction, skipping the
    /// function patterns - the order the standard lays the codewords down in.
    /// </summary>
    void DrawCodewords(byte[] codewords)
    {
        var index = 0;

        for (var right = this.Size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
                right = 5; // column 6 is the vertical timing pattern

            for (var step = 0; step < this.Size; step++)
            {
                for (var column = 0; column < 2; column++)
                {
                    var x = right - column;
                    var upwards = ((right + 1) & 2) == 0;
                    var y = upwards ? this.Size - 1 - step : step;

                    if (!this.isFunction[y * this.Size + x] && index < codewords.Length * 8)
                    {
                        this.modules[y * this.Size + x] = Bit(codewords[index >> 3], 7 - (index & 7));
                        index++;
                    }
                }
            }
        }
    }


    /// <summary>
    /// Every mask is legal; the standard scores them and takes the lowest, which is what keeps large
    /// blank areas and finder-lookalikes out of the data region.
    /// </summary>
    int ChooseMask()
    {
        var best = 0;
        var lowest = Int32.MaxValue;

        for (var mask = 0; mask < 8; mask++)
        {
            this.ApplyMask(mask);
            this.DrawFormat(mask);

            var penalty = this.Penalty();
            if (penalty < lowest)
            {
                lowest = penalty;
                best = mask;
            }
            this.ApplyMask(mask); // xor is its own undo
        }
        return best;
    }


    void ApplyMask(int mask)
    {
        for (var y = 0; y < this.Size; y++)
        {
            for (var x = 0; x < this.Size; x++)
            {
                if (this.isFunction[y * this.Size + x])
                    continue;

                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (y / 2 + x / 3) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0
                };

                this.modules[y * this.Size + x] ^= invert;
            }
        }
    }


    int Penalty()
    {
        var score = 0;
        var dark = 0;

        // runs of five or more, and anything that reads like a finder pattern, along both axes
        for (var pass = 0; pass < 2; pass++)
        {
            for (var line = 0; line < this.Size; line++)
            {
                var run = 0;
                var last = false;
                var window = 0;

                for (var i = 0; i < this.Size; i++)
                {
                    var module = pass == 0 ? this[i, line] : this[line, i];

                    if (i > 0 && module == last)
                    {
                        run++;
                        if (run == 5)
                            score += 3;
                        else if (run > 5)
                            score += 1;
                    }
                    else
                    {
                        run = 1;
                        last = module;
                    }

                    window = ((window << 1) & 0x7FF) | (module ? 1 : 0);
                    if (i >= 10 && (window == 0b10111010000 || window == 0b00001011101))
                        score += 40;

                    if (pass == 0 && module)
                        dark++;
                }
            }
        }

        for (var y = 0; y < this.Size - 1; y++)
        {
            for (var x = 0; x < this.Size - 1; x++)
            {
                var module = this[x, y];
                if (module == this[x + 1, y] && module == this[x, y + 1] && module == this[x + 1, y + 1])
                    score += 3;
            }
        }

        // and how far off an even split of dark and light the whole thing is
        var total = this.Size * this.Size;
        score += Math.Abs(dark * 100 / total - 50) / 5 * 10;

        return score;
    }


    void SetFunction(int x, int y, bool dark)
    {
        this.modules[y * this.Size + x] = dark;
        this.isFunction[y * this.Size + x] = true;
    }


    static bool Bit(int value, int index) => ((value >> index) & 1) != 0;
}

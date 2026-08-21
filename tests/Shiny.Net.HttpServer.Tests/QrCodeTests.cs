using System.Security.Cryptography;
using System.Text;
using Shiny.Net.HttpServer.CommandLine;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// A QR code is either right to the module or it does not scan, and nothing about the banner would
/// say which. The expectations here came out of an independent encoder - the `qrcode` npm package,
/// asked for level M and byte mode to match what this one does - rather than out of this one, so
/// they fail if the placement, the interleaving, the mask choice or the format bits drift.
/// </summary>
public class QrCodeTests
{
    const string Url = "http://192.168.1.24:8080/";

    /// <summary>Text, the version it should land on, and a digest of every module row.</summary>
    public static TheoryData<string, int, string> References => new()
    {
        { "http://1.2.3.4", 1, "773c0569b9221d965e4233ec5f5ef33c0e7e0cacfd008abf7895b8b14b9d5c2a" },
        { "http://10.0.0.5:8080/", 2, "6c7b3f5a6616edfaac6cbc0a1d0a0f5a2a7e1e67706fbef89f59fb04c7b77a62" },
        { Url, 2, "2cf7a1bbde9678f279d15187ac58878ce2230192ac0351de0d402aa0335f64ca" },
        { Url + new string('a', 17), 3, "86f366d501f0831366da83701286289c965d8d41584ab251721b435d3e6ef784" },

        // version 4 is where the payload starts being split into blocks, and 7 is where a code
        // starts carrying its own version number in the corners
        { "https://192.168.100.200:65535/files/shared/", 4, "3b51b10f8b286c6fb5ba9e2c68a9354c36132b41e6cf0f946a22b7a6fe1938d2" },
        { Url + new string('a', 40), 5, "259a3b3d294ff535882ca7e3b7f3f951a25e3c2dba097b3b702228fe2cb1810f" },
        { Url + new string('a', 90), 7, "cbbb96dd560460c6fb29bfd07e4fc8476629d43077ab97cb1a3140d76edb2c24" },
        { Url + new string('a', 188), 10, "66331a3672523db46cda327edc4a6bfb91c494f18a2e28019d438558cc46baf6" }
    };


    [Theory]
    [MemberData(nameof(References))]
    public void Encodes_what_a_reference_encoder_encodes(string text, int version, string digest)
    {
        Assert.True(QrCode.TryEncode(text, out var code));
        Assert.Equal(version, code.Version);
        Assert.Equal(version * 4 + 17, code.Size);
        Assert.Equal(digest, Digest(code));
    }


    /// <summary>The same check as above for the everyday case, spelled out so a failure is readable.</summary>
    [Fact]
    public void Draws_a_lan_url_module_for_module()
    {
        string[] expected =
        [
            "#######...###.#...#######",
            "#.....#...#..#.#..#.....#",
            "#.###.#.####....#.#.###.#",
            "#.###.#.#.####....#.###.#",
            "#.###.#.#.....#...#.###.#",
            "#.....#.#.#...###.#.....#",
            "#######.#.#.#.#.#.#######",
            "........##.....#.........",
            "#.#####...#.##.##.#####..",
            ".#.###.#..#####..#.....#.",
            "#...#.#.#.####.#..##.#.##",
            "...#.#.###......#...#...#",
            ".#.#..###...##....###.###",
            "#.#..#...#...##......#.#.",
            "#....##.#..##..#.#.#.#.##",
            "#...##.#...#...#.....#..#",
            "#.##..#...####..#####.#..",
            "........#...#.#.#...###..",
            "#######.........#.#.#####",
            "#.....#.####...##...##.##",
            "#.###.#.#..###.########..",
            "#.###.#.#########.###.###",
            "#.###.#.#.#.#....#....#.#",
            "#.....#.....#...#.####..#",
            "#######.#.#..#.#.########"
        ];

        Assert.True(QrCode.TryEncode(Url, out var code));
        Assert.Equal(expected, Rows(code, '#', '.'));
    }


    [Fact]
    public void Refuses_more_than_a_version_10_code_holds()
    {
        Assert.True(QrCode.TryEncode(new string('a', QrCode.MaxBytes), out _));
        Assert.False(QrCode.TryEncode(new string('a', QrCode.MaxBytes + 1), out var code));
        Assert.Null(code);
    }


    /// <summary>Multi-byte text is counted in bytes, not characters, or the length prefix lies.</summary>
    [Fact]
    public void Counts_a_payload_in_bytes()
    {
        Assert.True(QrCode.TryEncode(new string('é', QrCode.MaxBytes / 2), out _));
        Assert.False(QrCode.TryEncode(new string('é', QrCode.MaxBytes / 2 + 1), out _));
    }


    static string Digest(QrCode code)
        => Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(String.Join("\n", Rows(code, '1', '0'))))
        );


    static string[] Rows(QrCode code, char dark, char light)
    {
        var rows = new string[code.Size];

        for (var y = 0; y < code.Size; y++)
        {
            var builder = new StringBuilder(code.Size);

            for (var x = 0; x < code.Size; x++)
                builder.Append(code[x, y] ? dark : light);

            rows[y] = builder.ToString();
        }
        return rows;
    }
}


/// <summary>
/// The rendering half: a code that wraps, loses its quiet zone or comes out inverted is a code a
/// phone will not read, and none of that shows up in the encoder's own tests.
/// </summary>
public class QrConsoleTests
{
    static QrCode Code()
    {
        Assert.True(QrCode.TryEncode("http://192.168.1.24:8080/", out var code));
        return code;
    }


    /// <summary>Two module rows to a text row, plus the four module quiet zone on every side.</summary>
    [Fact]
    public void Halves_the_height_and_keeps_the_quiet_zone()
    {
        var code = Code();
        var lines = QrConsole.Render(code);

        Assert.Equal(code.Size + 8, QrConsole.Width(code));
        Assert.Equal((code.Size + 8 + 1) / 2, lines.Count);
        Assert.All(lines, x => Assert.Equal(code.Size + 8, x.Length));

        // four light module rows above and below - two text rows of pure quiet zone at each end
        Assert.All(lines.Take(2).Concat(lines.TakeLast(2)), x => Assert.Equal(new string(' ', x.Length), x));
        Assert.All(lines, x => Assert.Equal("    ", x[..4]));
        Assert.All(lines, x => Assert.Equal("    ", x[^4..]));
    }


    /// <summary>A version 2 code fits an 80 column window with room to spare, which is the whole point of half-blocks.</summary>
    [Fact]
    public void Fits_an_ordinary_terminal()
        => Assert.True(QrConsole.Width(Code()) <= 40);


    [Fact]
    public void Draws_with_nothing_but_block_glyphs()
        => Assert.All(QrConsole.Render(Code()), line => Assert.All(line.ToCharArray(), c => Assert.Contains(c, " █▀▄")));


    /// <summary>Each half-block has to carry the module it stands for - swapping them inverts the code.</summary>
    [Fact]
    public void Puts_the_right_module_in_each_half()
    {
        var code = Code();
        var lines = QrConsole.Render(code);

        for (var y = 0; y < code.Size; y++)
        {
            for (var x = 0; x < code.Size; x++)
            {
                var glyph = lines[(y + 4) / 2][x + 4];
                var dark = (y + 4) % 2 == 0 ? glyph is '█' or '▀' : glyph is '█' or '▄';

                Assert.Equal(code[x, y], dark);
            }
        }
    }
}

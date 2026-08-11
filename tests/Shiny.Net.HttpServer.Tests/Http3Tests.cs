using System.Buffers;
using System.Globalization;
using Shiny.Net.HttpServer.Http2.Hpack;
using Shiny.Net.HttpServer.Http3;
using Shiny.Net.HttpServer.Http3.Qpack;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The QUIC variable-length integer, checked against the worked examples in RFC 9000 §A.1.
/// <para>
/// Everything in HTTP/3 is measured in these — frame types, lengths, stream types — so an error
/// here is not a wrong number, it is a stream that decodes as something else entirely.
/// </para>
/// </summary>
public class VariableLengthIntegerTests
{
    static byte[] Encode(long value)
    {
        var buffer = new ArrayBufferWriter<byte>(8);
        VariableLengthInteger.Write(buffer, value);

        return buffer.WrittenSpan.ToArray();
    }

    static long Decode(string hex)
    {
        var bytes = Convert.FromHexString(hex);

        Assert.True(VariableLengthInteger.TryRead(bytes, out var value, out var consumed));
        Assert.Equal(bytes.Length, consumed);

        return value;
    }

    /// <summary>The four examples the RFC itself gives.</summary>
    [Theory]
    [InlineData("c2197c5eff14e88c", 151288809941952652L)]
    [InlineData("9d7f3e7d", 494878333L)]
    [InlineData("7bbd", 15293L)]
    [InlineData("25", 37L)]
    public void Decodes_the_rfc_examples(string hex, long expected) => Assert.Equal(expected, Decode(hex));

    /// <summary>The two-byte form of 37, which the RFC also lists — a decoder must accept both.</summary>
    [Fact]
    public void Accepts_a_longer_encoding_than_necessary() => Assert.Equal(37L, Decode("4025"));

    [Theory]
    [InlineData(0L, 1)]
    [InlineData(63L, 1)]
    [InlineData(64L, 2)]
    [InlineData(16383L, 2)]
    [InlineData(16384L, 4)]
    [InlineData(1073741823L, 4)]
    [InlineData(1073741824L, 8)]
    [InlineData(VariableLengthInteger.MaxValue, 8)]
    public void Uses_the_shortest_encoding(long value, int expectedLength)
    {
        var encoded = Encode(value);

        Assert.Equal(expectedLength, encoded.Length);
        Assert.True(VariableLengthInteger.TryRead(encoded, out var decoded, out _));
        Assert.Equal(value, decoded);
    }

    [Fact]
    public void Reports_a_partial_value_rather_than_guessing()
    {
        // A four-byte header with only two bytes present.
        Assert.False(VariableLengthInteger.TryRead(Convert.FromHexString("9d7f"), out _, out _));
        Assert.False(VariableLengthInteger.TryRead([], out _, out _));
    }

    [Fact]
    public void Refuses_values_outside_the_range()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Encode(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Encode(VariableLengthInteger.MaxValue + 1));
    }
}

public class Http3FrameTests
{
    [Fact]
    public void Round_trips_a_frame()
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        Http3Frame.Write(buffer, Http3FrameType.Data, "hello"u8);

        var sequence = new ReadOnlySequence<byte>(buffer.WrittenMemory);

        Assert.True(Http3Frame.TryReadHeader(sequence, out var header, out var consumed));
        Assert.Equal(Http3FrameType.Data, header.KnownType);
        Assert.Equal(5, header.Length);

        var payload = sequence.Slice(consumed);
        Assert.Equal("hello"u8.ToArray(), payload.ToArray());
    }

    [Fact]
    public void Waits_for_a_header_that_is_not_all_there()
    {
        // A type byte and nothing else.
        var sequence = new ReadOnlySequence<byte>(new byte[] { 0x01 });

        Assert.False(Http3Frame.TryReadHeader(sequence, out _, out _));
    }

    [Fact]
    public void Round_trips_settings()
    {
        var payload = Http3Frame.BuildSettings([(Http3SettingId.MaxFieldSectionSize, 16384), (Http3SettingId.QpackBlockedStreams, 0)]);

        Assert.True(Http3Frame.TryParseSettings(payload, out var settings));
        Assert.Equal(2, settings.Count);
        Assert.Equal(16384, settings.Single(s => s.Id == Http3SettingId.MaxFieldSectionSize).Value);
    }

    /// <summary>A repeated identifier is a connection error, not a last-one-wins.</summary>
    [Fact]
    public void Rejects_duplicate_settings()
    {
        var buffer = new ArrayBufferWriter<byte>(16);

        foreach (var value in new long[] { 1, 100, 1, 200 })
            VariableLengthInteger.Write(buffer, value);

        Assert.False(Http3Frame.TryParseSettings(buffer.WrittenSpan, out _));
    }

    [Fact]
    public void Rejects_a_truncated_settings_payload()
        => Assert.False(Http3Frame.TryParseSettings([0x01], out _));
}

public class QpackTests
{
    static List<HeaderField> Decode(byte[] payload) => new QpackDecoder().Decode(payload);

    static byte[] Encode(params HeaderField[] fields)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        new QpackEncoder().Encode(buffer, fields);

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The table is not HPACK's, and the indices are the whole contract — a shifted entry decodes
    /// into a different header, which is the single easiest way to get this wrong.
    /// </summary>
    [Theory]
    [InlineData(0, ":authority", "")]
    [InlineData(1, ":path", "/")]
    [InlineData(17, ":method", "GET")]
    [InlineData(23, ":scheme", "https")]
    [InlineData(25, ":status", "200")]
    [InlineData(31, "accept-encoding", "gzip, deflate, br")]
    [InlineData(52, "content-type", "text/html; charset=utf-8")]
    [InlineData(98, "x-frame-options", "sameorigin")]
    public void Static_table_matches_the_rfc(int index, string name, string value)
    {
        Assert.True(QpackStaticTable.TryGet(index, out var field));
        Assert.Equal(name, field.Name);
        Assert.Equal(value, field.Value);
    }

    [Fact]
    public void Static_table_has_the_expected_size() => Assert.Equal(99, QpackStaticTable.Count);

    [Fact]
    public void Round_trips_a_request_that_is_all_static_entries()
    {
        var decoded = Decode(Encode(
            new HeaderField(":method", "GET"),
            new HeaderField(":scheme", "https"),
            new HeaderField(":path", "/")
        ));

        Assert.Equal(3, decoded.Count);
        Assert.Equal(new HeaderField(":method", "GET"), decoded[0]);
        Assert.Equal(new HeaderField(":scheme", "https"), decoded[1]);
        Assert.Equal(new HeaderField(":path", "/"), decoded[2]);
    }

    [Fact]
    public void Round_trips_a_static_name_with_a_new_value()
    {
        var decoded = Decode(Encode(new HeaderField(":path", "/api/widgets?take=5")));

        Assert.Equal(new HeaderField(":path", "/api/widgets?take=5"), decoded.Single());
    }

    [Fact]
    public void Round_trips_a_header_the_table_has_never_heard_of()
    {
        var decoded = Decode(Encode(new HeaderField("x-shiny-device", "kitchen-tablet")));

        Assert.Equal(new HeaderField("x-shiny-device", "kitchen-tablet"), decoded.Single());
    }

    [Fact]
    public void Round_trips_a_realistic_response()
    {
        var fields = new[]
        {
            new HeaderField(":status", "200"),
            new HeaderField("content-type", "application/json"),
            new HeaderField("content-length", "1234"),
            new HeaderField("date", "Mon, 10 Aug 2026 19:00:00 GMT"),
            new HeaderField("x-request-id", "01J9Z7K3QW8N0P")
        };

        Assert.Equal(fields, Decode(Encode(fields)));
    }

    /// <summary>Huffman is only worth it when it is actually shorter, and both forms must decode.</summary>
    [Fact]
    public void Round_trips_values_on_both_sides_of_the_huffman_threshold()
    {
        foreach (var value in new[] { "a", "ab", "eeeeeeeeeeeeeeeeeeeeeeeeeeee", "ÿþý" })
        {
            var decoded = Decode(Encode(new HeaderField("x-test", value)));

            Assert.Equal(value, decoded.Single().Value);
        }
    }

    [Fact]
    public void Lowercases_header_names_on_the_way_out()
    {
        var decoded = Decode(Encode(new HeaderField("X-Mixed-Case", "v")));

        Assert.Equal("x-mixed-case", decoded.Single().Name);
    }

    [Fact]
    public void Round_trips_an_empty_field_section()
        => Assert.Empty(Decode(Encode()));

    /// <summary>
    /// The server announces a dynamic table capacity of zero, so a reference to one is a peer
    /// ignoring the settings — and decoding it as anything else would be worse than failing.
    /// </summary>
    [Fact]
    public void Refuses_a_dynamic_table_reference()
    {
        // Prefix (insert count 0, base 0) then 1T=0 — an indexed line against the dynamic table.
        var payload = new byte[] { 0x00, 0x00, 0x80 };

        Assert.Throws<QpackException>(() => Decode(payload));
    }

    [Fact]
    public void Refuses_a_nonzero_required_insert_count()
        => Assert.Throws<QpackException>(() => Decode([0x01, 0x00]));

    [Fact]
    public void Refuses_a_post_base_field_line()
        => Assert.Throws<QpackException>(() => Decode([0x00, 0x00, 0x10]));

    [Fact]
    public void Refuses_an_index_past_the_end_of_the_static_table()
    {
        var buffer = new ArrayBufferWriter<byte>(8);
        buffer.Write(new byte[] { 0x00, 0x00 });
        QpackInteger.Encode(buffer, 200, 6, 0xC0);

        Assert.Throws<QpackException>(() => Decode(buffer.WrittenSpan.ToArray()));
    }

    [Fact]
    public void Refuses_a_truncated_field_section()
    {
        Assert.Throws<QpackException>(() => Decode([]));
        Assert.Throws<QpackException>(() => Decode([0x00]));
    }

    /// <summary>A length that runs past the buffer must fail as a parse error, not an exception.</summary>
    [Fact]
    public void Refuses_a_string_that_claims_more_bytes_than_are_present()
    {
        // Prefix, then a literal-with-literal-name whose name claims 50 bytes.
        var payload = new byte[] { 0x00, 0x00, 0x20 | 0x07, 50, (byte)'x' };

        Assert.Throws<QpackException>(() => Decode(payload));
    }

    [Fact]
    public void Refuses_a_field_section_larger_than_the_limit()
    {
        var big = new string('v', 4096);
        var fields = Enumerable.Range(0, 40)
            .Select(i => new HeaderField("x-pad-" + i.ToString(CultureInfo.InvariantCulture), big))
            .ToArray();

        var payload = Encode(fields);

        Assert.Throws<QpackException>(() => new QpackDecoder(maxFieldSectionSize: 8 * 1024).Decode(payload));
    }

    /// <summary>Prefixed integers are the substrate for every representation above them.</summary>
    [Theory]
    [InlineData(0L, 7)]
    [InlineData(126L, 7)]
    [InlineData(127L, 7)]
    [InlineData(128L, 7)]
    [InlineData(1337L, 5)]
    [InlineData(100000L, 3)]
    public void Round_trips_a_prefixed_integer(long value, int prefixBits)
    {
        var buffer = new ArrayBufferWriter<byte>(16);
        QpackInteger.Encode(buffer, value, prefixBits, 0);

        Assert.True(QpackInteger.TryDecode(buffer.WrittenSpan, prefixBits, out var decoded, out var consumed));
        Assert.Equal(value, decoded);
        Assert.Equal(buffer.WrittenCount, consumed);
    }
}

/// <summary>
/// The listener itself. QUIC needs msquic, which .NET ships on Windows and Linux but not macOS, so
/// the live path is only exercised where the platform can run it.
/// </summary>
public class Http3ListenerTests
{
    [Fact]
    public void Reports_whether_the_platform_can_run_quic()
    {
        // Not an assertion about the value — it is a fact about the machine. What matters is that
        // asking does not throw, since everything else keys off it.
        _ = Http3Listener.IsSupported;
    }

    [Fact]
    public async Task Refuses_to_bind_without_a_certificate()
    {
        Assert.SkipUnless(Http3Listener.IsSupported, "QUIC is not supported on this platform.");

        await using var server = new HttpServer();
        await using var listener = new Http3Listener(server, new Http3Options { Port = 0 });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => listener.BindAsync(TestContext.Current.CancellationToken).AsTask()
        );
    }

    [Fact]
    public async Task Says_so_plainly_when_quic_is_unavailable()
    {
        Assert.SkipWhen(Http3Listener.IsSupported, "QUIC is supported here, so there is nothing to refuse.");

        await using var server = new HttpServer();
        await using var listener = new Http3Listener(server, new Http3Options());

        var ex = await Assert.ThrowsAsync<PlatformNotSupportedException>(
            () => listener.BindAsync(TestContext.Current.CancellationToken).AsTask()
        );

        Assert.Contains("msquic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Builds_an_alt_svc_value_clients_can_act_on()
    {
        var options = new Http3Options { Port = 8443 };

        Assert.Equal("h3=\":8443\"; ma=86400", options.BuildAltSvc());
        Assert.Equal("h3=\":443\"", new Http3Options { AltSvc = "h3=\":443\"" }.BuildAltSvc());
    }
}

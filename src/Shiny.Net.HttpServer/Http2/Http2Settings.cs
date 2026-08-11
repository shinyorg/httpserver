using System.Buffers;
using System.Buffers.Binary;

namespace Shiny.Net.HttpServer.Http2;

/// <summary>Setting identifiers (RFC 9113 §6.5.2). Unknown ones must be ignored.</summary>
public enum Http2SettingId : ushort
{
    HeaderTableSize = 0x1,
    EnablePush = 0x2,
    MaxConcurrentStreams = 0x3,
    InitialWindowSize = 0x4,
    MaxFrameSize = 0x5,
    MaxHeaderListSize = 0x6
}

/// <summary>
/// One end's settings.
/// <para>
/// Two sets are in play on every connection and they are not symmetric: ours constrain what the
/// peer may send us, theirs constrain what we may send them. Mixing the two up produces a server
/// that works against a permissive client and fails against a strict one.
/// </para>
/// </summary>
public sealed class Http2Settings
{
    /// <summary>The defaults every connection starts from, before any SETTINGS frame.</summary>
    public const int DefaultInitialWindowSize = 65_535;
    public const int DefaultMaxFrameSize = 16_384;
    public const int MinMaxFrameSize = 16_384;
    public const int MaxMaxFrameSize = 16_777_215;

    public int HeaderTableSize { get; set; } = 4_096;

    public bool EnablePush { get; set; } = true;

    /// <summary>Null means unlimited, which is the protocol default.</summary>
    public int? MaxConcurrentStreams { get; set; }

    public int InitialWindowSize { get; set; } = DefaultInitialWindowSize;

    public int MaxFrameSize { get; set; } = DefaultMaxFrameSize;

    public int? MaxHeaderListSize { get; set; }

    /// <summary>Writes these settings as a SETTINGS payload.</summary>
    public void Write(IBufferWriter<byte> writer)
    {
        // Only what differs from the defaults is worth sending; a peer applies defaults for
        // anything absent.
        Span<(Http2SettingId Id, uint Value)> settings = stackalloc (Http2SettingId, uint)[6];
        var count = 0;

        settings[count++] = (Http2SettingId.HeaderTableSize, (uint)this.HeaderTableSize);
        settings[count++] = (Http2SettingId.EnablePush, this.EnablePush ? 1u : 0u);

        if (this.MaxConcurrentStreams is { } concurrent)
            settings[count++] = (Http2SettingId.MaxConcurrentStreams, (uint)concurrent);

        settings[count++] = (Http2SettingId.InitialWindowSize, (uint)this.InitialWindowSize);
        settings[count++] = (Http2SettingId.MaxFrameSize, (uint)this.MaxFrameSize);

        if (this.MaxHeaderListSize is { } headerList)
            settings[count++] = (Http2SettingId.MaxHeaderListSize, (uint)headerList);

        var span = writer.GetSpan(count * 6);
        var offset = 0;

        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(span[offset..], (ushort)settings[i].Id);
            BinaryPrimitives.WriteUInt32BigEndian(span[(offset + 2)..], settings[i].Value);
            offset += 6;
        }

        writer.Advance(offset);
    }

    /// <summary>
    /// Applies a received SETTINGS payload, returning how much the initial window moved so the
    /// caller can adjust every open stream by the same delta (RFC 9113 §6.9.2).
    /// </summary>
    public int Apply(ReadOnlySequence<byte> payload)
    {
        if (payload.Length % 6 != 0)
            throw new Http2ConnectionException(Http2ErrorCode.FrameSizeError, "A SETTINGS frame must be a multiple of 6 bytes.");

        var previousWindow = this.InitialWindowSize;
        var reader = new SequenceReader<byte>(payload);
        Span<byte> entry = stackalloc byte[6];

        while (reader.Remaining >= 6)
        {
            reader.TryCopyTo(entry);
            reader.Advance(6);

            var id = (Http2SettingId)BinaryPrimitives.ReadUInt16BigEndian(entry);
            var value = BinaryPrimitives.ReadUInt32BigEndian(entry[2..]);

            switch (id)
            {
                case Http2SettingId.HeaderTableSize:
                    this.HeaderTableSize = (int)Math.Min(value, int.MaxValue);
                    break;

                case Http2SettingId.EnablePush:
                    if (value > 1)
                        throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "ENABLE_PUSH must be 0 or 1.");

                    this.EnablePush = value == 1;
                    break;

                case Http2SettingId.MaxConcurrentStreams:
                    this.MaxConcurrentStreams = (int)Math.Min(value, int.MaxValue);
                    break;

                case Http2SettingId.InitialWindowSize:
                    if (value > int.MaxValue)
                        throw new Http2ConnectionException(Http2ErrorCode.FlowControlError, "INITIAL_WINDOW_SIZE is above 2^31-1.");

                    this.InitialWindowSize = (int)value;
                    break;

                case Http2SettingId.MaxFrameSize:
                    if (value is < MinMaxFrameSize or > MaxMaxFrameSize)
                        throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "MAX_FRAME_SIZE is out of range.");

                    this.MaxFrameSize = (int)value;
                    break;

                case Http2SettingId.MaxHeaderListSize:
                    this.MaxHeaderListSize = (int)Math.Min(value, int.MaxValue);
                    break;

                // Anything else is a setting from a later revision. Ignoring it is required, not
                // merely permitted.
            }
        }

        return this.InitialWindowSize - previousWindow;
    }
}

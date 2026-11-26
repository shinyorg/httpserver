using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>One frame as it came off the wire.</summary>
sealed record Http2RawFrame(int Length, byte Type, byte Flags, uint StreamId, byte[] Payload)
{
    public const byte Data = 0x0;
    public const byte Headers = 0x1;
    public const byte RstStream = 0x3;
    public const byte Settings = 0x4;
    public const byte Ping = 0x6;
    public const byte GoAway = 0x7;
    public const byte WindowUpdate = 0x8;
    public const byte Continuation = 0x9;

    public bool EndStream => (this.Type is Data or Headers) && (this.Flags & 0x1) != 0;

    /// <summary>The error code of a RST_STREAM, or of a GOAWAY.</summary>
    public uint ErrorCode => this.Type == GoAway
        ? BinaryPrimitives.ReadUInt32BigEndian(this.Payload.AsSpan(4))
        : BinaryPrimitives.ReadUInt32BigEndian(this.Payload);
}

/// <summary>
/// HTTP/2 spoken frame by frame over a raw socket, for everything <see cref="HttpClient"/> will not
/// do on purpose: resetting streams in bulk, withholding flow-control credit, sending more than the
/// window, splitting a header block forever, lying about Content-Length. It acks SETTINGS and
/// records every frame; it grants no credit unless told to.
/// </summary>
sealed class Http2RawClient : IAsyncDisposable
{
    readonly Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
    readonly SemaphoreSlim sendGate = new(1, 1);
    readonly ConcurrentQueue<Http2RawFrame> frames = new();
    readonly SemaphoreSlim frameArrived = new(0);
    long totalData;
    Task? reader;

    public IReadOnlyCollection<Http2RawFrame> Frames => this.frames;

    /// <summary>Every DATA payload byte received, across all streams.</summary>
    public long TotalData => Interlocked.Read(ref this.totalData);

    /// <summary>Called on the reader's thread for every frame, after it is recorded.</summary>
    public Action<Http2RawFrame>? OnFrame { get; set; }

    /// <summary>True once the server closed the connection.</summary>
    public bool Closed { get; private set; }

    public static async Task<Http2RawClient> ConnectAsync(int port, int? initialWindow = null)
    {
        var client = new Http2RawClient();
        client.socket.NoDelay = true;
        await client.socket.ConnectAsync(IPAddress.Loopback, port);
        await client.SendRawAsync("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray());

        var settings = new List<byte>();
        if (initialWindow is { } window)
            AddSetting(settings, 0x4, (uint)window);

        await client.SendFrameAsync(Http2RawFrame.Settings, 0, 0, settings.ToArray());
        client.reader = Task.Run(client.ReadLoopAsync);

        // The server's SETTINGS, and so our ack, are out of the way before the test sends anything:
        // an ack arriving between HEADERS and CONTINUATION would be a protocol error of its own.
        await client.WaitForAsync(x => x.Type == Http2RawFrame.Settings && (x.Flags & 0x1) == 0);

        return client;
    }

    public static void AddSetting(List<byte> settings, ushort id, uint value)
    {
        var entry = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(entry, id);
        BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(2), value);
        settings.AddRange(entry);
    }

    async Task ReadLoopAsync()
    {
        var header = new byte[9];
        try
        {
            while (true)
            {
                await this.ReadExactAsync(header);
                var length = (header[0] << 16) | (header[1] << 8) | header[2];
                var payload = new byte[length];
                await this.ReadExactAsync(payload);

                var frame = new Http2RawFrame(
                    length,
                    header[3],
                    header[4],
                    BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5)) & 0x7FFFFFFF,
                    payload
                );

                if (frame.Type == Http2RawFrame.Data)
                    Interlocked.Add(ref this.totalData, length);

                // Acked before the frame is published, so a test that has seen the server's
                // SETTINGS knows the ack is not about to land in the middle of what it sends next.
                if (frame.Type == Http2RawFrame.Settings && (frame.Flags & 0x1) == 0)
                    await this.SendFrameAsync(Http2RawFrame.Settings, 0x1, 0, []);

                this.frames.Enqueue(frame);

                this.OnFrame?.Invoke(frame);
                this.frameArrived.Release();
            }
        }
        catch
        {
        }

        this.Closed = true;
        this.frameArrived.Release();
    }

    async Task ReadExactAsync(byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await this.socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None);
            if (read == 0)
                throw new IOException("closed");

            offset += read;
        }
    }

    /// <summary>Waits for a frame matching <paramref name="match"/>, or null on timeout or close.</summary>
    public async Task<Http2RawFrame?> WaitForAsync(Func<Http2RawFrame, bool> match, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));

        while (true)
        {
            var found = this.frames.FirstOrDefault(match);
            if (found is not null)
                return found;

            var remaining = deadline - DateTime.UtcNow;
            if (this.Closed || remaining <= TimeSpan.Zero)
                return null;

            await this.frameArrived.WaitAsync(remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>Waits for the server to close the connection.</summary>
    public async Task<bool> WaitForCloseAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!this.Closed && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        return this.Closed;
    }

    public Task<Http2RawFrame?> WaitForGoAwayAsync(TimeSpan? timeout = null)
        => this.WaitForAsync(x => x.Type == Http2RawFrame.GoAway, timeout);

    public Task<Http2RawFrame?> WaitForResetAsync(uint streamId, TimeSpan? timeout = null)
        => this.WaitForAsync(x => x.Type == Http2RawFrame.RstStream && x.StreamId == streamId, timeout);

    public Task<Http2RawFrame?> WaitForEndAsync(uint streamId, TimeSpan? timeout = null)
        => this.WaitForAsync(x => x.StreamId == streamId && (x.EndStream || x.Type == Http2RawFrame.RstStream), timeout);

    public long DataBytes(uint streamId) => this.frames
        .Where(x => x.Type == Http2RawFrame.Data && x.StreamId == streamId)
        .Sum(x => (long)x.Length);

    public async Task SendRawAsync(byte[] data)
    {
        await this.sendGate.WaitAsync();
        try
        {
            var offset = 0;
            while (offset < data.Length)
                offset += await this.socket.SendAsync(data.AsMemory(offset), SocketFlags.None);
        }
        finally
        {
            this.sendGate.Release();
        }
    }

    public static byte[] BuildFrame(byte type, byte flags, uint streamId, ReadOnlySpan<byte> payload)
    {
        var buffer = new byte[9 + payload.Length];
        buffer[0] = (byte)(payload.Length >> 16);
        buffer[1] = (byte)(payload.Length >> 8);
        buffer[2] = (byte)payload.Length;
        buffer[3] = type;
        buffer[4] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(5), streamId);
        payload.CopyTo(buffer.AsSpan(9));

        return buffer;
    }

    public Task SendFrameAsync(byte type, byte flags, uint streamId, byte[] payload)
        => this.SendRawAsync(BuildFrame(type, flags, streamId, payload));

    public Task WindowUpdateAsync(uint streamId, int increment)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)increment);

        return this.SendFrameAsync(Http2RawFrame.WindowUpdate, 0, streamId, payload);
    }

    public Task ResetAsync(uint streamId, uint code = 8)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, code);

        return this.SendFrameAsync(Http2RawFrame.RstStream, 0, streamId, payload);
    }

    public Task GoAwayAsync(uint lastStreamId, uint code = 0)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(payload, lastStreamId);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), code);

        return this.SendFrameAsync(Http2RawFrame.GoAway, 0, 0, payload);
    }

    public Task PingAsync() => this.SendFrameAsync(Http2RawFrame.Ping, 0, 0, new byte[8]);

    public Task DataAsync(uint streamId, int length, bool endStream)
        => this.SendFrameAsync(Http2RawFrame.Data, endStream ? (byte)0x1 : (byte)0, streamId, new byte[length]);

    /// <summary>Sends <paramref name="length"/> body bytes in frames of the protocol's minimum maximum size.</summary>
    public async Task BodyAsync(uint streamId, int length, bool endStream)
    {
        while (length > 0)
        {
            var size = Math.Min(length, 16 * 1024);
            length -= size;
            await this.DataAsync(streamId, size, endStream && length == 0);
        }
    }

    public static void WriteInteger(List<byte> output, int value, int prefixBits, byte flags)
    {
        var mask = (1 << prefixBits) - 1;
        if (value < mask)
        {
            output.Add((byte)(flags | value));
            return;
        }

        output.Add((byte)(flags | mask));
        value -= mask;

        while (value >= 0x80)
        {
            output.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        output.Add((byte)value);
    }

    public static void WriteString(List<byte> output, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        WriteInteger(output, bytes.Length, 7, 0);
        output.AddRange(bytes);
    }

    /// <summary>Adds a literal header without indexing, with a literal name.</summary>
    public static void WriteHeader(List<byte> output, string name, string value)
    {
        WriteInteger(output, 0, 4, 0);
        WriteString(output, name);
        WriteString(output, value);
    }

    /// <summary>The request pseudo-headers, as literals without indexing.</summary>
    public static List<byte> RequestBlock(string method, string path)
    {
        var output = new List<byte>();
        WriteInteger(output, 2, 4, 0);
        WriteString(output, method);
        WriteInteger(output, 6, 4, 0);
        WriteString(output, "http");
        WriteInteger(output, 4, 4, 0);
        WriteString(output, path);
        WriteInteger(output, 1, 4, 0);
        WriteString(output, "localhost");

        return output;
    }

    public Task HeadersAsync(uint streamId, string method, string path, bool endStream, params (string Name, string Value)[] headers)
    {
        var block = RequestBlock(method, path);
        foreach (var (name, value) in headers)
            WriteHeader(block, name, value);

        return this.SendFrameAsync(Http2RawFrame.Headers, (byte)(0x4 | (endStream ? 0x1 : 0)), streamId, block.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            this.socket.Shutdown(SocketShutdown.Both);
        }
        catch
        {
        }

        this.socket.Dispose();

        if (this.reader is not null)
            await this.reader;
    }
}

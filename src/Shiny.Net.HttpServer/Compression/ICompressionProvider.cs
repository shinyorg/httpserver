using System.IO.Compression;

namespace Shiny.Net.HttpServer.Compression;

/// <summary>One content coding the server can produce.</summary>
public interface ICompressionProvider
{
    /// <summary>The token used in <c>Accept-Encoding</c> and <c>Content-Encoding</c>.</summary>
    string EncodingName { get; }

    /// <summary>
    /// Tie-break when a client expresses no preference between codings it accepts. Higher wins.
    /// </summary>
    int Priority { get; }

    /// <summary>Wraps the response body. The returned stream is disposed when the body ends.</summary>
    Stream CreateStream(Stream output, CompressionLevel level);
}

/// <summary>
/// Brotli. Compresses better than gzip at comparable speed, and every current browser accepts it —
/// but only over HTTPS in some, which is why gzip stays registered alongside.
/// </summary>
public sealed class BrotliCompressionProvider : ICompressionProvider
{
    public string EncodingName => "br";

    public int Priority => 100;

    public Stream CreateStream(Stream output, CompressionLevel level) => new BrotliStream(output, level, leaveOpen: true);
}

/// <summary>Gzip. Universally understood, which is the whole argument for it.</summary>
public sealed class GzipCompressionProvider : ICompressionProvider
{
    public string EncodingName => "gzip";

    public int Priority => 50;

    public Stream CreateStream(Stream output, CompressionLevel level) => new GZipStream(output, level, leaveOpen: true);
}

/// <summary>
/// Raw deflate. Registered last because implementations disagree about whether it means zlib or
/// bare deflate, and some old clients get it wrong — gzip is the same algorithm without the
/// ambiguity.
/// </summary>
public sealed class DeflateCompressionProvider : ICompressionProvider
{
    public string EncodingName => "deflate";

    public int Priority => 10;

    public Stream CreateStream(Stream output, CompressionLevel level) => new ZLibStream(output, level, leaveOpen: true);
}

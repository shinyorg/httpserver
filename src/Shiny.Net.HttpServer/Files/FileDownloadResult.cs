using System.Globalization;

namespace Shiny.Net.HttpServer.Files;

/// <summary>One byte range a client asked for.</summary>
public readonly record struct ByteRange(long From, long To)
{
    public long Length => this.To - this.From + 1;
}

/// <summary>
/// Parses <c>Range</c> headers.
/// <para>
/// Only the single-range form is honoured. Multi-range responses need a
/// <c>multipart/byteranges</c> body, and a server that answers one badly is worse than one that
/// declines: a client that asked for several ranges and gets the whole entity back still works,
/// because 200 is always a valid answer to a range request.
/// </para>
/// </summary>
public static class RangeHeader
{
    /// <summary>
    /// Parses a range against a known entity length. Returns false for "no usable range" — either
    /// absent, malformed, or multi-range. <paramref name="unsatisfiable"/> distinguishes a range
    /// that was well-formed but entirely past the end, which is a 416 rather than a 200.
    /// </summary>
    public static bool TryParse(string? header, long length, out ByteRange range, out bool unsatisfiable)
    {
        range = default;
        unsatisfiable = false;

        if (string.IsNullOrWhiteSpace(header) || length <= 0)
            return false;

        const string prefix = "bytes=";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var spec = header[prefix.Length..].Trim();

        // Several ranges: decline rather than answer the first one, which would be a lie about
        // what was asked for.
        if (spec.Contains(','))
            return false;

        var dash = spec.IndexOf('-');
        if (dash < 0)
            return false;

        var fromText = spec[..dash].Trim();
        var toText = spec[(dash + 1)..].Trim();

        if (fromText.Length == 0)
        {
            // "-500" means the last 500 bytes.
            if (!long.TryParse(toText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
                return false;

            var start = Math.Max(0, length - suffix);
            range = new ByteRange(start, length - 1);

            return true;
        }

        if (!long.TryParse(fromText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var from) || from < 0)
            return false;

        if (from >= length)
        {
            unsatisfiable = true;
            return false;
        }

        if (toText.Length == 0)
        {
            range = new ByteRange(from, length - 1);
            return true;
        }

        if (!long.TryParse(toText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var to) || to < from)
            return false;

        range = new ByteRange(from, Math.Min(to, length - 1));
        return true;
    }
}

/// <summary>
/// A download that supports resumption and conditional requests.
/// <para>
/// Range support is what makes a large download recoverable and a media file seekable; without it a
/// dropped connection means starting again. The conditional headers are the other half — a client
/// that already has the file should be told so with a 304 rather than handed it a second time.
/// </para>
/// </summary>
public sealed class FileDownloadResult : IActionResult
{
    readonly Func<CancellationToken, ValueTask<Stream>> open;

    FileDownloadResult(Func<CancellationToken, ValueTask<Stream>> open, long length)
    {
        this.open = open;
        this.Length = length;
    }

    /// <summary>Total entity length, which is what ranges are resolved against.</summary>
    public long Length { get; }

    public string ContentType { get; init; } = "application/octet-stream";

    /// <summary>Sets <c>Content-Disposition</c>. Null serves the file inline.</summary>
    public string? FileDownloadName { get; init; }

    /// <summary>Served inline rather than as an attachment when a download name is set.</summary>
    public bool Inline { get; init; }

    /// <summary>Entity tag for conditional requests. Quoted automatically if it is not already.</summary>
    public string? ETag { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    /// <summary>Streams a file from disk, with its length and modification time filled in.</summary>
    public static FileDownloadResult FromFile(string path, string? contentType = null, string? downloadName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("The file does not exist.", path);

        return new FileDownloadResult(
            _ => new ValueTask<Stream>(new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                }
            )),
            info.Length
        )
        {
            ContentType = contentType ?? ContentTypes.ForFileName(path),
            FileDownloadName = downloadName ?? Path.GetFileName(path),

            // Weak-ish but effective: a file whose length and mtime are both unchanged is, for
            // caching purposes, the same file.
            ETag = $"\"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}\"",
            LastModified = info.LastWriteTimeUtc
        };
    }

    /// <summary>Serves bytes already in memory.</summary>
    public static FileDownloadResult FromBytes(byte[] content, string contentType = "application/octet-stream", string? downloadName = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        return new FileDownloadResult(_ => new ValueTask<Stream>(new MemoryStream(content, writable: false)), content.LongLength)
        {
            ContentType = contentType,
            FileDownloadName = downloadName
        };
    }

    /// <summary>
    /// Serves content from a factory, opened only once the response is actually going to be written.
    /// <para>
    /// The difference from <see cref="FromStream"/> is when the handle is taken. A resolver that
    /// looks up many candidates — a static file handler trying default documents — would otherwise
    /// open and discard a stream for each miss, and a conditional request that ends in a 304 opens
    /// nothing at all.
    /// </para>
    /// </summary>
    public static FileDownloadResult FromOpener(
        Func<CancellationToken, ValueTask<Stream>> open,
        long length,
        string contentType = "application/octet-stream",
        string? downloadName = null,
        string? eTag = null,
        DateTimeOffset? lastModified = null
    )
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        return new FileDownloadResult(open, length)
        {
            ContentType = contentType,
            FileDownloadName = downloadName,
            ETag = eTag,
            LastModified = lastModified
        };
    }

    /// <summary>
    /// Serves a seekable stream of known length. The stream is disposed once the response is
    /// written, and must support seeking for ranges to work.
    /// </summary>
    public static FileDownloadResult FromStream(Stream stream, long length, string contentType = "application/octet-stream", string? downloadName = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return new FileDownloadResult(_ => new ValueTask<Stream>(stream), length)
        {
            ContentType = contentType,
            FileDownloadName = downloadName
        };
    }

    public async ValueTask ExecuteAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Request;
        var response = context.Response;
        var cancellationToken = context.RequestAborted;

        var etag = Normalize(this.ETag);

        if (etag is not null)
            response.Headers[HeaderNames.ETag] = etag;

        if (this.LastModified is { } modified)
            response.Headers[HeaderNames.LastModified] = modified.ToString("R", CultureInfo.InvariantCulture);

        // Advertised before anything else, so a client knows resumption is possible even on the
        // first, unranged request.
        response.Headers[HeaderNames.AcceptRanges] = "bytes";

        if (this.FileDownloadName is { Length: > 0 } name)
            response.Headers[HeaderNames.ContentDisposition] = ContentDisposition.ForDownload(name, this.Inline);

        if (this.IsNotModified(request, etag))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            response.ContentLength = 0;

            await response.StartAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // A range is only honoured when If-Range still matches; otherwise the entity has changed
        // under the client and continuing its download would splice two different files together.
        var range = default(ByteRange);
        var unsatisfiable = false;

        var wantsRange = this.RangeIsFresh(request, etag)
            && RangeHeader.TryParse(request.Headers.GetFirst(HeaderNames.Range), this.Length, out range, out unsatisfiable);

        if (!wantsRange && unsatisfiable)
        {
            response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            response.Headers[HeaderNames.ContentRange] = $"bytes */{this.Length}";
            response.ContentLength = 0;

            await response.StartAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var stream = await this.open(cancellationToken).ConfigureAwait(false);

        await using (stream.ConfigureAwait(false))
        {
            response.ContentType = this.ContentType;

            if (!wantsRange)
            {
                response.ContentLength = this.Length;
                await response.StartAsync(cancellationToken).ConfigureAwait(false);
                await stream.CopyToAsync(response.Body, cancellationToken).ConfigureAwait(false);

                return;
            }

            response.StatusCode = StatusCodes.Status206PartialContent;
            response.Headers[HeaderNames.ContentRange] = $"bytes {range.From}-{range.To}/{this.Length}";
            response.ContentLength = range.Length;

            await response.StartAsync(cancellationToken).ConfigureAwait(false);

            if (stream.CanSeek)
                stream.Seek(range.From, SeekOrigin.Begin);
            else
                await SkipAsync(stream, range.From, cancellationToken).ConfigureAwait(false);

            await CopyAsync(stream, response.Body, range.Length, cancellationToken).ConfigureAwait(false);
        }
    }

    bool IsNotModified(HttpRequest request, string? etag)
    {
        if (request.Headers.GetFirst(HeaderNames.IfNoneMatch) is { Length: > 0 } ifNoneMatch && etag is not null)
        {
            if (ifNoneMatch.Trim() == "*")
                return true;

            foreach (var candidate in ifNoneMatch.Split(','))
            {
                var trimmed = candidate.Trim();

                // A weak comparison ignores the W/ prefix, which is what If-None-Match calls for.
                if (trimmed.StartsWith("W/", StringComparison.Ordinal))
                    trimmed = trimmed[2..];

                if (trimmed == etag)
                    return true;
            }

            // An explicit If-None-Match that did not match settles it; falling through to the date
            // check could contradict the tag the client actually holds.
            return false;
        }

        return this.LastModified is { } modified
            && request.Headers.GetFirst(HeaderNames.IfModifiedSince) is { Length: > 0 } since
            && DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
            // Second precision on the wire, so anything within a second counts as unchanged.
            && modified.ToUnixTimeSeconds() <= parsed.ToUnixTimeSeconds();
    }

    bool RangeIsFresh(HttpRequest request, string? etag)
    {
        if (request.Headers.GetFirst(HeaderNames.IfRange) is not { Length: > 0 } ifRange)
            return true;

        var value = ifRange.Trim();

        if (value.StartsWith('"') || value.StartsWith("W/\"", StringComparison.Ordinal))
            return etag is not null && value == etag;

        return this.LastModified is { } modified
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
            && modified.ToUnixTimeSeconds() == parsed.ToUnixTimeSeconds();
    }

    static string? Normalize(string? etag)
    {
        if (etag is not { Length: > 0 })
            return null;

        return etag.StartsWith('"') || etag.StartsWith("W/\"", StringComparison.Ordinal) ? etag : $"\"{etag}\"";
    }

    static async ValueTask SkipAsync(Stream stream, long count, CancellationToken cancellationToken)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (count > 0)
            {
                var take = (int)Math.Min(count, buffer.Length);
                var read = await stream.ReadAsync(buffer.AsMemory(0, take), cancellationToken).ConfigureAwait(false);

                if (read == 0)
                    return;

                count -= read;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    static async ValueTask CopyAsync(Stream source, Stream destination, long count, CancellationToken cancellationToken)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (count > 0)
            {
                var take = (int)Math.Min(count, buffer.Length);
                var read = await source.ReadAsync(buffer.AsMemory(0, take), cancellationToken).ConfigureAwait(false);

                if (read == 0)
                    return;

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                count -= read;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>Content types for the extensions a file server actually meets.</summary>
public static class ContentTypes
{
    static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain; charset=utf-8",
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".xml"] = "application/xml; charset=utf-8",
        [".csv"] = "text/csv; charset=utf-8",
        [".md"] = "text/markdown; charset=utf-8",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".avif"] = "image/avif",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip",
        [".gz"] = "application/gzip",
        [".wasm"] = "application/wasm",

        // .NET on WebAssembly. A Blazor app is mostly these: assemblies are shipped as .wasm, ICU
        // globalization data as .dat, and without a type for them the runtime cannot start.
        [".dat"] = "application/octet-stream",
        [".blat"] = "application/octet-stream",
        [".webcil"] = "application/octet-stream",
        [".dll"] = "application/octet-stream",
        [".pdb"] = "application/octet-stream",
        [".symbols"] = "application/octet-stream",

        // Served as JSON so a browser will parse rather than download them.
        [".webmanifest"] = "application/manifest+json"
    };

    /// <summary>
    /// The content type for a path's extension, or <c>application/octet-stream</c>.
    /// <para>
    /// Unknown types deliberately fall back to a type browsers will not render. Guessing
    /// <c>text/html</c> for an unknown extension is how an upload directory becomes a
    /// cross-site-scripting vector.
    /// </para>
    /// </summary>
    public static string ForFileName(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        return ByExtension.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");
    }

    /// <summary>
    /// Whether an extension is in the map at all.
    /// <para>
    /// <see cref="ForFileName"/> answers <c>application/octet-stream</c> for both a <c>.bin</c> and
    /// a <c>.wat</c>, so its return value cannot tell "known binary" from "no idea". A static file
    /// handler has to know the difference to decide whether serving it is safe.
    /// </para>
    /// </summary>
    public static bool IsKnownExtension(string extension) =>
        extension.Length > 0 && ByExtension.ContainsKey(extension);
}

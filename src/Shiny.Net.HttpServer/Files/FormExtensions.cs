using System.Text;
using Microsoft.Extensions.Primitives;

namespace Shiny.Net.HttpServer.Files;

/// <summary>A file that arrived in a multipart form and was buffered.</summary>
public sealed class FormFile(string name, string? fileName, string? contentType, byte[] content)
{
    /// <summary>The form field name.</summary>
    public string Name { get; } = name;

    /// <summary>The client's file name, already stripped of any directory component.</summary>
    public string? FileName { get; } = fileName;

    public string? ContentType { get; } = contentType;

    public byte[] Content { get; } = content;

    public long Length => this.Content.LongLength;

    public Stream OpenReadStream() => new MemoryStream(this.Content, writable: false);

    public async Task SaveToAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Options = FileOptions.Asynchronous
            }
        );

        await file.WriteAsync(this.Content, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A parsed form: plain values, and files if it was multipart.</summary>
public sealed class FormCollection
{
    internal FormCollection(Dictionary<string, StringValues> values, List<FormFile> files)
    {
        this.values = values;
        this.Files = files;
    }

    readonly Dictionary<string, StringValues> values;

    public static readonly FormCollection Empty = new(new(StringComparer.OrdinalIgnoreCase), []);

    public IReadOnlyList<FormFile> Files { get; }

    public StringValues this[string key] => this.values.TryGetValue(key, out var value) ? value : StringValues.Empty;

    public int Count => this.values.Count;

    public ICollection<string> Keys => this.values.Keys;

    public bool ContainsKey(string key) => this.values.ContainsKey(key);

    public string? GetFirst(string key) => this.values.TryGetValue(key, out var value) ? value.ToString() : null;

    /// <summary>The first file for a field name, or null.</summary>
    public FormFile? GetFile(string name)
        => this.Files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Reading form bodies and uploads.</summary>
public static class FormExtensions
{
    /// <summary>True when the body is a form this server can parse.</summary>
    public static bool HasFormContentType(this HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.ContentType is { } contentType
            && (contentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Streams the parts of a <c>multipart/form-data</c> body.
    /// <para>
    /// This is the one to use for real uploads: each part's body goes straight to its destination
    /// and nothing large is ever held in memory. <see cref="ReadFormAsync"/> buffers, which is fine
    /// for a handful of small fields and wrong for a video.
    /// </para>
    /// <code>
    /// await foreach (var part in ctx.Request.ReadMultipartAsync())
    /// {
    ///     if (part.IsFile)
    ///         await part.SaveToAsync(Path.Combine(uploads, part.SafeFileName()!));
    /// }
    /// </code>
    /// </summary>
    public static async IAsyncEnumerable<MultipartSection> ReadMultipartAsync(
        this HttpRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var boundary = MultipartReader.GetBoundary(request.ContentType)
            ?? throw new BadHttpRequestException("The request is not multipart/form-data, or has no boundary.");

        var reader = new MultipartReader(request.BodyReader, boundary);

        while (await reader.ReadNextSectionAsync(cancellationToken).ConfigureAwait(false) is { } section)
            yield return section;
    }

    /// <summary>The uploaded file name with any directory component removed, or null.</summary>
    public static string? SafeFileName(this MultipartSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        return ContentDisposition.Parse(section.Headers.GetFirst(HeaderNames.ContentDisposition)).SafeFileName;
    }

    /// <summary>
    /// Reads the whole form into memory — values, and files up to
    /// <paramref name="maxFileSize"/> each.
    /// <para>
    /// Convenient, and bounded on purpose: buffering an upload of unknown size is how a server runs
    /// out of memory. Stream with <see cref="ReadMultipartAsync"/> when the files are real.
    /// </para>
    /// </summary>
    public static async ValueTask<FormCollection> ReadFormAsync(
        this HttpRequest request,
        int maxValueLength = 64 * 1024,
        int maxFileSize = 8 * 1024 * 1024,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ContentType is not { } contentType)
            return FormCollection.Empty;

        if (contentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            var body = await request.ReadBodyAsStringAsync(maxValueLength, cancellationToken).ConfigureAwait(false);
            return new FormCollection(ParseUrlEncoded(body), []);
        }

        if (MultipartReader.GetBoundary(contentType) is null)
            return FormCollection.Empty;

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
        var files = new List<FormFile>();

        await foreach (var section in request.ReadMultipartAsync(cancellationToken).ConfigureAwait(false))
        {
            var disposition = ContentDisposition.Parse(section.Headers.GetFirst(HeaderNames.ContentDisposition));
            var name = disposition.Name;

            if (name is null)
                continue;

            if (disposition.FileName is null)
            {
                var value = await section.ReadAsStringAsync(maxValueLength, cancellationToken).ConfigureAwait(false);
                values[name] = values.TryGetValue(name, out var existing)
                    ? StringValues.Concat(existing, value)
                    : new StringValues(value);

                continue;
            }

            using var buffer = new MemoryStream();
            var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(8192);

            try
            {
                int read;
                while ((read = await section.Body.ReadAsync(rented, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > maxFileSize)
                        throw new BadHttpRequestException(
                            $"An uploaded file exceeds the {maxFileSize} byte limit.",
                            StatusCodes.Status413PayloadTooLarge
                        );

                    buffer.Write(rented, 0, read);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }

            files.Add(new FormFile(name, disposition.SafeFileName, section.ContentType, buffer.ToArray()));
        }

        return new FormCollection(values, files);
    }

    static Dictionary<string, StringValues> ParseUrlEncoded(string body)
    {
        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var key = Decode(equals < 0 ? pair : pair[..equals]);
            var value = equals < 0 ? string.Empty : Decode(pair[(equals + 1)..]);

            if (key.Length == 0)
                continue;

            values[key] = values.TryGetValue(key, out var existing)
                ? StringValues.Concat(existing, value)
                : new StringValues(value);
        }

        return values;

        // '+' means space in form encoding, which Uri.UnescapeDataString does not know.
        static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
    }

    /// <summary>
    /// Streams the raw request body straight to a file, for a <c>PUT</c>-style upload with no
    /// multipart wrapper. Never buffers.
    /// </summary>
    public static async Task<long> SaveBodyToAsync(
        this HttpRequest request,
        string path,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(path);

        await using var file = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            }
        );

        await request.Body.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        return file.Length;
    }
}

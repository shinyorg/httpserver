using System.Buffers;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.Net.HttpServer;

public static class HttpContextExtensions
{
    /// <summary>
    /// Resolves a service from the request scope. Scoped services resolved here are the same
    /// instances the rest of this request sees, and are disposed when the request ends.
    /// </summary>
    public static T GetRequiredService<T>(this HttpContext context) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.RequestServices.GetRequiredService<T>();
    }

    public static T? GetService<T>(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (T?)context.RequestServices.GetService(typeof(T));
    }

    /// <summary>
    /// Buffers the whole request body into a byte array. Convenient, but it holds the entire payload
    /// in memory — stream from <see cref="HttpRequest.Body"/> for large uploads.
    /// </summary>
    public static async ValueTask<byte[]> ReadBodyAsBytesAsync(
        this HttpRequest request,
        int maxLength = 1024 * 1024,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ContentLength > maxLength)
            throw new BadHttpRequestException(
                $"Request body of {request.ContentLength} bytes exceeds the {maxLength} byte limit.",
                StatusCodes.Status413PayloadTooLarge
            );

        // A known length lets us size the buffer exactly and avoid the MemoryStream copy.
        if (request.ContentLength is { } length)
        {
            var exact = new byte[length];
            var offset = 0;
            while (offset < exact.Length)
            {
                var read = await request.Body
                    .ReadAsync(exact.AsMemory(offset), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                    throw new BadHttpRequestException("Request body ended before Content-Length was satisfied.");

                offset += read;
            }
            return exact;
        }

        using var buffer = new MemoryStream();
        var rented = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int bytesRead;
            while ((bytesRead = await request.Body.ReadAsync(rented, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + bytesRead > maxLength)
                    throw new BadHttpRequestException(
                        $"Request body exceeds the {maxLength} byte limit.",
                        StatusCodes.Status413PayloadTooLarge
                    );

                buffer.Write(rented, 0, bytesRead);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
        return buffer.ToArray();
    }

    /// <summary>Buffers and decodes the request body as UTF-8 text.</summary>
    public static async ValueTask<string> ReadBodyAsStringAsync(
        this HttpRequest request,
        int maxLength = 1024 * 1024,
        CancellationToken cancellationToken = default
    )
    {
        var bytes = await request.ReadBodyAsBytesAsync(maxLength, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// The client IP. With <paramref name="useForwardedHeaders"/>, the <em>right-most</em>
    /// <c>X-Forwarded-For</c> entry — the address the nearest proxy saw — when there is one. Only trust this
    /// when the server is genuinely behind a proxy or tunnel that appends to the header.
    /// <para>
    /// Not the left-most: every proxy appends, so the left-hand end is whatever the client chose to send.
    /// Taking it let any caller pick their own address through an ordinary appending proxy. For a chain of
    /// proxies, configure <see cref="HttpServerOptions.ForwardedHeaders"/> and read
    /// <see cref="ConnectionInfo.RemoteIpAddress"/> instead.
    /// </para>
    /// </summary>
    public static IPAddress? GetClientIpAddress(this HttpContext context, bool useForwardedHeaders = false)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (useForwardedHeaders && ForwardedHeaders.RightMostForwardedFor(context) is { } forwarded)
            return forwarded;

        return context.Connection.RemoteIpAddress;
    }
}

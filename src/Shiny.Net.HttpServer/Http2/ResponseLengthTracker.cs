namespace Shiny.Net.HttpServer.Http2;

/// <summary>
/// Holds an HTTP/2 or HTTP/3 response to the Content-Length it declared, the way the HTTP/1.1
/// output does.
/// <para>
/// END_STREAM (or a QUIC FIN) is what really ends the body on these protocols, so a wrong length
/// does not desynchronise the connection - but the client still checks the body against it, and a
/// file that grew or shrank while it was being served would otherwise arrive as a clean response
/// with the wrong bytes in it. Too many bytes fails before the extra ones are sent; too few resets
/// the stream instead of ending it cleanly, so the client knows the body is incomplete.
/// </para>
/// </summary>
sealed class ResponseLengthTracker
{
    long? declared;

    /// <summary>Body bytes the handler has written, sent or not.</summary>
    public long Written { get; private set; }

    /// <summary>Captures the declared length when the response starts.</summary>
    public void Begin(HttpResponse response)
        => this.declared = IsBodyForbidden(response.StatusCode) ? null : response.ContentLength;

    /// <summary>Counts bytes about to be written, throwing before they go out if they overrun.</summary>
    public void Track(int count)
    {
        this.Written += count;

        if (this.declared is { } declared && this.Written > declared)
            throw new InvalidOperationException(
                $"Wrote {this.Written} body bytes but Content-Length declared {declared}."
            );
    }

    /// <summary>
    /// Whether the body would end short of the declared length once <paramref name="pending"/>
    /// more bytes are written.
    /// </summary>
    public bool IsShort(long pending) => this.declared is { } declared && this.Written + pending < declared;

    /// <summary>The error for a body that ended short, once everything written has been counted.</summary>
    public InvalidOperationException Shortfall() => new(
        $"Content-Length declared {this.declared} bytes but only {this.Written} were written."
    );

    /// <summary>Whether a response that has written nothing yet still owes a body.</summary>
    public static bool DeclaresBody(HttpResponse response)
        => !IsBodyForbidden(response.StatusCode) && response.ContentLength > 0;

    /// <summary>1xx, 204 and 304 have no body, whatever Content-Length says.</summary>
    static bool IsBodyForbidden(int status)
        => status is StatusCodes.Status204NoContent or StatusCodes.Status304NotModified
            || status is >= 100 and < 200;
}

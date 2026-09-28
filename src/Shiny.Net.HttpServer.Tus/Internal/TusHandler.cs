using System.Globalization;


namespace Shiny.Net.HttpServer.Tus.Internal;

/// <summary>
/// The verbs behind <see cref="TusExtensions.MapTus(HttpServer, string, TusOptions)"/>.
/// <para>
/// Every request that names an upload takes that upload's lease first and holds it to the end, so
/// the store only ever sees one request per upload - a <c>HEAD</c> included, because an offset read
/// while a stalled <c>PATCH</c> is still writing is an offset the next <c>PATCH</c> will be refused
/// for.
/// </para>
/// </summary>
sealed class TusHandler
{
    const string DeferLengthValue = "1";
    const int Status410Gone = 410;

    readonly TusOptions options;
    readonly ITusStore store;
    readonly string basePath;
    readonly TusLockManager locks = new();
    readonly string extensions;

    long lastSweep;
    int sweeping;
    Timer? timer;

    public TusHandler(TusOptions options, ITusStore store, string basePath)
    {
        this.options = options;
        this.store = store;
        this.basePath = basePath;
        this.lastSweep = options.TimeProvider.GetTimestamp();

        var list = new List<string> { "creation", "creation-with-upload" };

        if (options.AllowDeferredLength)
            list.Add("creation-defer-length");

        if (options.AllowTermination)
            list.Add("termination");

        if (options.Expiration is not null)
            list.Add("expiration");

        list.Add("checksum");

        if (options.AllowConcatenation)
            list.Add("concatenation");

        this.extensions = string.Join(",", list);
    }

    DateTimeOffset Now => this.options.TimeProvider.GetUtcNow();

    // ---- plumbing every verb goes through ----

    /// <summary>
    /// Wraps a verb with what every tus response needs, and turns a <see cref="TusException"/> -
    /// from here or from a store - into the status it names.
    /// </summary>
    public RequestDelegate Guard(RequestDelegate verb, bool requireVersion = true) => async context =>
    {
        var response = context.Response;
        response.Headers.Set(TusHeaderNames.TusResumable, TusProtocol.Version);

        // A browser hides every non-safelisted header from cross-origin script, and a tus client
        // that cannot read Upload-Offset or Location cannot work at all. Added only when the CORS
        // middleware has already allowed the origin - its OnStarting ran first - so this never
        // widens a policy, only completes one that forgot the protocol's headers.
        response.OnStarting(() =>
        {
            ExposeTusHeaders(response);
            return ValueTask.CompletedTask;
        });

        this.MaybeSweep();

        try
        {
            if (requireVersion && context.Request.Headers.GetFirst(TusHeaderNames.TusResumable)?.Trim() != TusProtocol.Version)
            {
                response.Headers.Set(TusHeaderNames.TusVersion, TusProtocol.Version);
                throw new TusException(
                    StatusCodes.Status412PreconditionFailed,
                    $"This server speaks tus {TusProtocol.Version}; send it as Tus-Resumable."
                );
            }

            await verb(context).ConfigureAwait(false);
        }
        catch (TusException ex) when (!response.HasStarted)
        {
            await ProblemAsync(context, ex.StatusCode, ex.Message).ConfigureAwait(false);
        }
    };

    static void ExposeTusHeaders(HttpResponse response)
    {
        if (response.Headers.GetFirst(HeaderNames.AccessControlAllowOrigin) is null)
            return;

        var existing = response.Headers.GetFirst(HeaderNames.AccessControlExposeHeaders);
        var names = new List<string>();

        if (!string.IsNullOrWhiteSpace(existing))
            names.AddRange(existing.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (var name in TusHeaderNames.Exposed)
        {
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }

        response.Headers.Set(HeaderNames.AccessControlExposeHeaders, string.Join(", ", names));
    }

    // ---- OPTIONS ----

    /// <summary>
    /// Discovery: what this server speaks and what it will accept. The one request that needs no
    /// <c>Tus-Resumable</c>, since asking which versions exist is how a client finds out.
    /// </summary>
    public ValueTask OptionsAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        headers.Set(TusHeaderNames.TusVersion, TusProtocol.Version);
        headers.Set(TusHeaderNames.TusExtension, this.extensions);
        headers.Set(TusHeaderNames.TusChecksumAlgorithm, string.Join(",", TusProtocol.ChecksumAlgorithms));

        if (this.options.MaxSize is { } max)
            headers.Set(TusHeaderNames.TusMaxSize, max.ToString(CultureInfo.InvariantCulture));

        return StatusAsync(context, StatusCodes.Status204NoContent);
    }

    // ---- POST (creation) ----

    public async ValueTask CreateAsync(HttpContext context)
    {
        var headers = context.Request.Headers;
        var (concatenation, parts) = this.ParseConcat(headers.GetFirst(TusHeaderNames.UploadConcat));

        var lengthHeader = headers.GetFirst(TusHeaderNames.UploadLength);
        var deferHeader = headers.GetFirst(TusHeaderNames.UploadDeferLength);
        var hasBody = IsOffsetStream(context.Request);
        long? length;

        if (concatenation == TusConcatenation.Final)
        {
            // A final upload's length is its parts', and its bytes are theirs - anything the client
            // says about either is a contradiction waiting to happen.
            if (lengthHeader is not null || deferHeader is not null || hasBody)
                throw new TusException(
                    StatusCodes.Status400BadRequest,
                    "A final upload takes its length and its bytes from its partial uploads."
                );

            length = await this.MeasurePartsAsync(parts, context.RequestAborted).ConfigureAwait(false);
        }
        else if (lengthHeader is not null)
        {
            if (deferHeader is not null)
                throw new TusException(StatusCodes.Status400BadRequest, "Send Upload-Length or Upload-Defer-Length, not both.");

            length = ParseLength(lengthHeader, TusHeaderNames.UploadLength);
        }
        else if (deferHeader is not null)
        {
            if (!this.options.AllowDeferredLength)
                throw new TusException(StatusCodes.Status400BadRequest, "This server needs Upload-Length when an upload is created.");

            if (deferHeader.Trim() != DeferLengthValue)
                throw new TusException(StatusCodes.Status400BadRequest, "Upload-Defer-Length must be 1.");

            length = null;
        }
        else
        {
            throw new TusException(StatusCodes.Status400BadRequest, "Upload-Length or Upload-Defer-Length is required.");
        }

        this.CheckMaxSize(length);

        if (!TusMetadata.TryParse(headers.GetFirst(TusHeaderNames.UploadMetadata), out var metadata))
            throw new TusException(
                StatusCodes.Status400BadRequest,
                "Upload-Metadata must be a comma-separated list of 'key base64value' pairs with unique keys."
            );

        // Parsed before anything is created, so a bad checksum header is a clean 400 and not an
        // upload that exists with nothing in it.
        var checksum = hasBody ? ParseChecksum(headers.GetFirst(TusHeaderNames.UploadChecksum)) : null;

        if (this.options.OnBeforeCreateAsync is { } before)
        {
            var creating = new TusCreateContext(context, length, metadata, concatenation);
            await before(creating).ConfigureAwait(false);

            if (creating.IsRejected)
            {
                await ProblemAsync(context, creating.RejectStatusCode, creating.RejectDetail).ConfigureAwait(false);
                return;
            }
        }

        DateTimeOffset? expires = concatenation != TusConcatenation.Final && this.options.Expiration is { } lifetime
            ? this.Now + lifetime
            : null;

        var id = await this.store.CreateAsync(new TusCreateRequest
        {
            Length = length,
            Metadata = metadata,
            ExpiresUtc = expires,
            Concatenation = concatenation,
            PartialUploads = parts
        }, context.RequestAborted).ConfigureAwait(false);

        var response = context.Response;
        response.Headers.Set(HeaderNames.Location, this.LocationFor(context, id));

        using var lease = await this.locks.AcquireAsync(id, TimeSpan.Zero, context.RequestAborted).ConfigureAwait(false);

        if (concatenation == TusConcatenation.Final)
            await this.store.ConcatenateAsync(id, parts, context.RequestAborted).ConfigureAwait(false);

        if (hasBody && lease is not null && await this.store.GetAsync(id, context.RequestAborted).ConfigureAwait(false) is { } created)
        {
            // creation-with-upload: the first chunk rides along with the POST, saving a round trip
            // that on a mobile link can cost more than the chunk. The upload exists whatever happens
            // to the body - the response still has to be a 201 carrying its Location, or the client
            // has no URL to resume against - so a rejected body is reported as an offset of zero
            // rather than as an error.
            AppendOutcome outcome;

            try
            {
                outcome = await this.AppendAsync(context, created, lease, checksum).ConfigureAwait(false);
            }
            catch (TusException)
            {
                outcome = new AppendOutcome(created.Offset, Interrupted: false, Preempted: false);
            }

            if (outcome.Interrupted)
            {
                await AbandonAsync(context, outcome).ConfigureAwait(false);
                return;
            }

            response.Headers.Set(TusHeaderNames.UploadOffset, outcome.Offset.ToString(CultureInfo.InvariantCulture));
        }

        if (expires is { } at)
            response.Headers.Set(TusHeaderNames.UploadExpires, at.ToString("r", CultureInfo.InvariantCulture));

        await this.CompleteIfFinishedAsync(context, id).ConfigureAwait(false);
        await StatusAsync(context, StatusCodes.Status201Created).ConfigureAwait(false);
    }

    (TusConcatenation, IReadOnlyList<string>) ParseConcat(string? header)
    {
        if (header is null)
            return (TusConcatenation.None, []);

        if (!this.options.AllowConcatenation)
            throw new TusException(StatusCodes.Status400BadRequest, "This server does not accept Upload-Concat.");

        header = header.Trim();

        if (header.Equals("partial", StringComparison.Ordinal))
            return (TusConcatenation.Partial, []);

        if (!header.StartsWith("final;", StringComparison.Ordinal))
            throw new TusException(StatusCodes.Status400BadRequest, "Upload-Concat must be 'partial' or 'final;<urls>'.");

        var parts = new List<string>();

        foreach (var url in header["final;".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            parts.Add(this.IdFromUrl(url) ?? throw new TusException(
                StatusCodes.Status400BadRequest,
                $"'{url}' is not an upload on this endpoint."
            ));
        }

        if (parts.Count == 0)
            throw new TusException(StatusCodes.Status400BadRequest, "A final upload needs at least one partial upload.");

        return (TusConcatenation.Final, parts);
    }

    /// <summary>
    /// The upload a URL in <c>Upload-Concat</c> names - absolute or origin-relative, as long as it
    /// is one of this endpoint's.
    /// </summary>
    string? IdFromUrl(string url)
    {
        string path;

        if (url.StartsWith('/'))
        {
            var cut = url.AsSpan().IndexOfAny('?', '#');
            path = cut < 0 ? url : url[..cut];
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            path = absolute.AbsolutePath;
        }
        else
        {
            return null;
        }

        var prefix = this.basePath + "/";

        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var id = path[prefix.Length..].TrimEnd('/');

        return IsPlausibleId(id) ? id : null;
    }

    async ValueTask<long> MeasurePartsAsync(IReadOnlyList<string> parts, CancellationToken cancellationToken)
    {
        long total = 0;

        foreach (var id in parts)
        {
            var part = await this.store.GetAsync(id, cancellationToken).ConfigureAwait(false);

            if (part is null || part.Concatenation != TusConcatenation.Partial)
                throw new TusException(StatusCodes.Status400BadRequest, $"'{id}' is not a partial upload on this endpoint.");

            // Joining unfinished parts is the concatenation-unfinished extension, which this server
            // does not offer: the final upload is built in one go, from parts that are all there.
            if (!part.IsComplete)
                throw new TusException(StatusCodes.Status400BadRequest, $"Partial upload '{id}' is not finished.");

            total += part.Length!.Value;
        }

        return total;
    }

    // ---- HEAD ----

    public async ValueTask HeadAsync(HttpContext context)
    {
        using var lease = await this.LeaseAsync(context).ConfigureAwait(false);
        var upload = await this.RequireUploadAsync(context, lease.Id).ConfigureAwait(false);

        var headers = context.Response.Headers;

        // The whole point of this response is to be fresh: a cached offset is a resume from the
        // wrong byte.
        headers.Set(HeaderNames.CacheControl, "no-store");
        headers.Set(TusHeaderNames.UploadOffset, upload.Offset.ToString(CultureInfo.InvariantCulture));

        if (upload.Length is { } length)
            headers.Set(TusHeaderNames.UploadLength, length.ToString(CultureInfo.InvariantCulture));
        else
            headers.Set(TusHeaderNames.UploadDeferLength, DeferLengthValue);

        if (upload.Metadata.Count > 0)
            headers.Set(TusHeaderNames.UploadMetadata, upload.Metadata.Header);

        if (this.ConcatHeaderFor(upload) is { } concat)
            headers.Set(TusHeaderNames.UploadConcat, concat);

        if (!upload.IsComplete && upload.ExpiresUtc is { } expires)
            headers.Set(TusHeaderNames.UploadExpires, expires.ToString("r", CultureInfo.InvariantCulture));

        await StatusAsync(context, StatusCodes.Status200OK).ConfigureAwait(false);
    }

    string? ConcatHeaderFor(TusUpload upload) => upload.Concatenation switch
    {
        TusConcatenation.Partial => "partial",
        TusConcatenation.Final => "final;" + string.Join(' ', upload.PartialUploads.Select(id => this.basePath + "/" + id)),
        _ => null
    };

    // ---- PATCH ----

    public async ValueTask PatchAsync(HttpContext context)
    {
        if (!IsOffsetStream(context.Request))
            throw new TusException(
                StatusCodes.Status415UnsupportedMediaType,
                $"A PATCH body must be sent as {TusProtocol.OffsetOctetStream}."
            );

        var offsetHeader = context.Request.Headers.GetFirst(TusHeaderNames.UploadOffset)
            ?? throw new TusException(StatusCodes.Status400BadRequest, "Upload-Offset is required.");

        var offset = ParseLength(offsetHeader, TusHeaderNames.UploadOffset);
        var checksum = ParseChecksum(context.Request.Headers.GetFirst(TusHeaderNames.UploadChecksum));

        using var lease = await this.LeaseAsync(context).ConfigureAwait(false);
        var upload = await this.RequireUploadAsync(context, lease.Id).ConfigureAwait(false);

        if (upload.Concatenation == TusConcatenation.Final)
            throw new TusException(StatusCodes.Status403Forbidden, "A final upload is assembled from its parts and cannot be written to.");

        // Noted before anything changes, so completion is raised on the request that crosses the
        // line and not again for an empty PATCH to an upload that was already finished.
        var wasComplete = upload.IsComplete;

        upload = await this.ApplyDeferredLengthAsync(context, upload).ConfigureAwait(false);

        if (offset != upload.Offset)
            throw new TusException(
                StatusCodes.Status409Conflict,
                $"Upload-Offset {offset} does not match the {upload.Offset} bytes already stored. HEAD the upload and resume from there."
            );

        var outcome = await this.AppendAsync(context, upload, lease, checksum).ConfigureAwait(false);

        if (outcome.Interrupted)
        {
            await AbandonAsync(context, outcome).ConfigureAwait(false);
            return;
        }

        var complete = upload.Length is { } length && outcome.Offset >= length;
        var headers = context.Response.Headers;

        headers.Set(TusHeaderNames.UploadOffset, outcome.Offset.ToString(CultureInfo.InvariantCulture));

        if (!complete && this.options.Expiration is { } lifetime)
        {
            // Sliding: an upload that is still making progress is not abandoned, however long it has
            // been going. Only one that stops is.
            var expires = this.Now + lifetime;
            await this.store.SetExpirationAsync(upload.Id, expires, context.RequestAborted).ConfigureAwait(false);
            headers.Set(TusHeaderNames.UploadExpires, expires.ToString("r", CultureInfo.InvariantCulture));
        }

        if (complete && !wasComplete)
            await this.CompleteIfFinishedAsync(context, upload.Id).ConfigureAwait(false);

        await StatusAsync(context, StatusCodes.Status204NoContent).ConfigureAwait(false);
    }

    /// <summary>
    /// An <c>Upload-Length</c> on a <c>PATCH</c> is how a client that deferred its length finally
    /// states it. It may do so once; after that, the length is what it said.
    /// </summary>
    async ValueTask<TusUpload> ApplyDeferredLengthAsync(HttpContext context, TusUpload upload)
    {
        if (context.Request.Headers.GetFirst(TusHeaderNames.UploadLength) is not { } header)
            return upload;

        var length = ParseLength(header, TusHeaderNames.UploadLength);

        if (upload.Length is { } known)
        {
            if (known != length)
                throw new TusException(StatusCodes.Status400BadRequest, "Upload-Length cannot change once it is set.");

            return upload;
        }

        if (length < upload.Offset)
            throw new TusException(StatusCodes.Status400BadRequest, "Upload-Length is shorter than what has already been uploaded.");

        this.CheckMaxSize(length);

        await this.store.SetLengthAsync(upload.Id, length, context.RequestAborted).ConfigureAwait(false);

        return (await this.store.GetAsync(upload.Id, context.RequestAborted).ConfigureAwait(false))!;
    }

    readonly record struct AppendOutcome(long Offset, bool Interrupted, bool Preempted);

    /// <summary>
    /// Streams the body into the store. Never buffers it: a request is copied from the connection to
    /// the store a buffer at a time, however big it is.
    /// </summary>
    async ValueTask<AppendOutcome> AppendAsync(
        HttpContext context,
        TusUpload upload,
        TusLockManager.Lease lease,
        TusChecksum? checksum
    )
    {
        var remaining = upload.Length is { } length
            ? length - upload.Offset
            : this.options.MaxSize is { } max ? max - upload.Offset : long.MaxValue;

        // Refused before a byte is read when the client says up front that it will overrun.
        if (context.Request.ContentLength is { } declared && declared > remaining)
            throw new TusException(StatusCodes.Status413PayloadTooLarge, "The body runs past the upload's length.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lease.ReleaseRequested);
        await using var body = new TusBodyStream(context.Request.Body, remaining, checksum, linked.Token);

        long offset;

        try
        {
            // Not the request's token: an interruption ends the *read*, and the store should finish
            // writing what it has and report where that got to - which is the whole feature.
            offset = await this.store.AppendAsync(upload.Id, upload.Offset, body, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TusException) when (body.Interrupted)
        {
            // Interrupted with a checksum: the store rolled back, and nobody is listening for the
            // 460 anyway.
            offset = upload.Offset;
        }

        return new AppendOutcome(offset, body.Interrupted, lease.ReleaseRequested.IsCancellationRequested);
    }

    /// <summary>
    /// Ends a request whose body stopped short. Its bytes are already stored; what is left is not
    /// leaving a half-read request on the connection.
    /// </summary>
    static async ValueTask AbandonAsync(HttpContext context, AppendOutcome outcome)
    {
        // HTTP/1.1 cannot recover a connection whose request body was abandoned mid-way - the next
        // bytes on it are the rest of this body, not a request - so it is closed. HTTP/2 and 3 end
        // just this stream, and closing the connection there would take the request that pre-empted
        // this one down with it, since a client resuming reuses the connection it has.
        if (context.Request.Protocol.StartsWith("HTTP/1", StringComparison.Ordinal))
        {
            context.Abort();
            return;
        }

        try
        {
            await ProblemAsync(
                context,
                StatusCodes.Status423Locked,
                outcome.Preempted
                    ? "Another request took this upload over. What this one sent before then was kept."
                    : "The request ended before its body did. What arrived was kept."
            ).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The client that would have read this is usually the one that went away.
        }
    }

    // ---- DELETE ----

    public async ValueTask DeleteAsync(HttpContext context)
    {
        if (!this.options.AllowTermination)
        {
            context.Response.Headers.Set(HeaderNames.Allow, "OPTIONS, HEAD, PATCH, POST");
            throw new TusException(StatusCodes.Status405MethodNotAllowed, "This server does not accept termination.");
        }

        using var lease = await this.LeaseAsync(context).ConfigureAwait(false);

        if (!await this.store.DeleteAsync(lease.Id, context.RequestAborted).ConfigureAwait(false))
            throw new TusException(StatusCodes.Status404NotFound, "The upload does not exist.");

        await StatusAsync(context, StatusCodes.Status204NoContent).ConfigureAwait(false);
    }

    // ---- POST on an upload (X-HTTP-Method-Override) ----

    /// <summary>
    /// A <c>POST</c> to an upload's URL is only meaningful as a tunnelled <c>PATCH</c>,
    /// <c>DELETE</c> or <c>HEAD</c>. The method is rewritten, so whatever runs after this - and a
    /// log line - sees the verb the client meant.
    /// </summary>
    public ValueTask OverrideAsync(HttpContext context)
    {
        var method = context.Request.Headers.GetFirst(TusHeaderNames.XHttpMethodOverride)?.Trim().ToUpperInvariant();

        return method switch
        {
            HttpMethods.Patch => this.PatchAsync(context),
            HttpMethods.Delete => this.DeleteAsync(context),
            HttpMethods.Head => this.HeadAsync(context),
            _ => throw new TusException(
                StatusCodes.Status405MethodNotAllowed,
                "POST to an upload needs X-HTTP-Method-Override: PATCH, DELETE or HEAD."
            )
        };
    }

    // ---- completion ----

    async ValueTask CompleteIfFinishedAsync(HttpContext context, string id)
    {
        if (this.options.OnUploadCompleteAsync is not { } complete)
            return;

        var upload = await this.store.GetAsync(id, context.RequestAborted).ConfigureAwait(false);

        if (upload is null || !upload.IsComplete || upload.Concatenation == TusConcatenation.Partial)
            return;

        await complete(new TusCompleteContext(context, upload, this.store)).ConfigureAwait(false);
    }

    // ---- expiry ----

    /// <summary>Deletes every unfinished upload past its expiry. Returns how many went.</summary>
    public async ValueTask<int> RemoveExpiredAsync(CancellationToken cancellationToken)
    {
        var now = this.Now;
        var removed = 0;

        foreach (var id in await this.store.GetExpiredAsync(now, cancellationToken).ConfigureAwait(false))
        {
            // Never waits and never pre-empts: an upload somebody is writing to right now is, by
            // definition, not abandoned - and the write will slide its expiry forward.
            using var lease = await this.locks.AcquireAsync(id, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
            if (lease is null)
                continue;

            if (await this.store.DeleteAsync(id, cancellationToken).ConfigureAwait(false))
                removed++;
        }

        return removed;
    }

    /// <summary>
    /// Nudges a sweep from request traffic, at most once per <see cref="TusOptions.CleanupInterval"/>
    /// and never on the request's own time.
    /// </summary>
    void MaybeSweep()
    {
        if (this.options.Expiration is null)
            return;

        var last = Interlocked.Read(ref this.lastSweep);
        if (this.options.TimeProvider.GetElapsedTime(last) < this.options.CleanupInterval)
            return;

        this.SweepInBackground();
    }

    void SweepInBackground()
    {
        if (Interlocked.CompareExchange(ref this.sweeping, 1, 0) != 0)
            return;

        Interlocked.Exchange(ref this.lastSweep, this.options.TimeProvider.GetTimestamp());

        _ = Task.Run(async () =>
        {
            try
            {
                await this.RemoveExpiredAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A sweep that fails is retried by the next one; there is nobody to report it to.
            }
            finally
            {
                Volatile.Write(ref this.sweeping, 0);
            }
        });
    }

    /// <summary>Runs the sweep on a timer for as long as <paramref name="server"/> is running.</summary>
    public void AttachTo(HttpServer server)
    {
        if (this.options.Expiration is null)
            return;

        void Update(HttpServerState state)
        {
            lock (this.locks)
            {
                if (state == HttpServerState.Running && this.timer is null)
                {
                    this.timer = new Timer(
                        _ => this.SweepInBackground(),
                        null,
                        this.options.CleanupInterval,
                        this.options.CleanupInterval
                    );
                }
                else if (state == HttpServerState.Stopped && this.timer is not null)
                {
                    this.timer.Dispose();
                    this.timer = null;
                }
            }
        }

        server.StateChanged += (_, state) => Update(state);
        Update(server.State);
    }

    // ---- shared ----

    /// <summary>
    /// The upload named by the route, taken for the rest of the request - asking any request that
    /// holds it to let go, as <see cref="TusOptions.LockReleaseTimeout"/> describes.
    /// </summary>
    async ValueTask<TusLockManager.Lease> LeaseAsync(HttpContext context)
    {
        var id = context.Request.RouteValues.TryGetValue("id", out var value) ? value?.ToString() : null;

        if (!IsPlausibleId(id))
            throw new TusException(StatusCodes.Status404NotFound, "The upload does not exist.");

        return await this.locks.AcquireAsync(id!, this.options.LockReleaseTimeout, context.RequestAborted).ConfigureAwait(false)
            ?? throw new TusException(
                StatusCodes.Status423Locked,
                "Another request is using this upload. Try again shortly."
            );
    }

    async ValueTask<TusUpload> RequireUploadAsync(HttpContext context, string id)
    {
        var upload = await this.store.GetAsync(id, context.RequestAborted).ConfigureAwait(false)
            ?? throw new TusException(StatusCodes.Status404NotFound, "The upload does not exist.");

        if (!upload.IsComplete && upload.ExpiresUtc is { } expires && expires < this.Now)
        {
            await this.store.DeleteAsync(id, context.RequestAborted).ConfigureAwait(false);
            throw new TusException(Status410Gone, "The upload expired before it was finished. Start it again.");
        }

        return upload;
    }

    /// <summary>
    /// A cheap first filter on identifiers arriving in a URL. The store is still the one that decides
    /// what an identifier means; this only keeps separators and dot-segments from ever reaching it.
    /// </summary>
    static bool IsPlausibleId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 256 || id is "." or "..")
            return false;

        foreach (var c in id)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.' and not '~')
                return false;
        }

        return true;
    }

    string LocationFor(HttpContext context, string id)
    {
        var path = this.basePath + "/" + id;
        var host = context.Request.Host;

        // Absolute where the request says where it was sent, since not every client resolves a
        // relative Location - and through a tunnel or a proxy that forwards Host, the absolute URL
        // is the public one.
        return string.IsNullOrEmpty(host) ? path : $"{context.Request.Scheme}://{host}{path}";
    }

    void CheckMaxSize(long? length)
    {
        if (length is { } value && this.options.MaxSize is { } max && value > max)
            throw new TusException(
                StatusCodes.Status413PayloadTooLarge,
                $"The upload is larger than the {max} bytes this server accepts."
            );
    }

    static bool IsOffsetStream(HttpRequest request)
        => request.ContentType is { } type
            && type.Split(';')[0].Trim().Equals(TusProtocol.OffsetOctetStream, StringComparison.OrdinalIgnoreCase);

    static long ParseLength(string value, string header)
        => long.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new TusException(StatusCodes.Status400BadRequest, $"{header} must be a non-negative integer.");

    static TusChecksum? ParseChecksum(string? header)
    {
        if (header is null)
            return null;

        return TusChecksum.TryParse(header, out var checksum)
            ? checksum
            : throw new TusException(
                StatusCodes.Status400BadRequest,
                $"Upload-Checksum must be '<algorithm> <base64 digest>' with one of: {string.Join(", ", TusProtocol.ChecksumAlgorithms)}."
            );
    }

    static ValueTask StatusAsync(HttpContext context, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentLength = 0;

        return context.Response.StartAsync(context.RequestAborted);
    }

    /// <summary>
    /// A problem response (RFC 9457), as the rest of the server answers errors. A tus client only
    /// reads the status; the body is for whoever is reading the logs. A <c>HEAD</c> gets the status
    /// alone.
    /// </summary>
    static ValueTask ProblemAsync(HttpContext context, int statusCode, string? detail)
    {
        if (HttpMethods.IsHead(context.Request.Method))
            return StatusAsync(context, statusCode);

        return ProblemDetailsWriter.WriteResponseAsync(context, new ProblemDetails
        {
            Status = statusCode,
            Title = statusCode switch
            {
                StatusCodes.Status400BadRequest => "Bad Request",
                StatusCodes.Status403Forbidden => "Forbidden",
                StatusCodes.Status404NotFound => "Not Found",
                StatusCodes.Status405MethodNotAllowed => "Method Not Allowed",
                StatusCodes.Status409Conflict => "Conflict",
                Status410Gone => "Gone",
                StatusCodes.Status412PreconditionFailed => "Precondition Failed",
                StatusCodes.Status413PayloadTooLarge => "Content Too Large",
                StatusCodes.Status415UnsupportedMediaType => "Unsupported Media Type",
                StatusCodes.Status423Locked => "Locked",
                TusProtocol.Status460ChecksumMismatch => "Checksum Mismatch",
                _ => null
            },
            Detail = detail
        }, context.RequestAborted);
    }
}

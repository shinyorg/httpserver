using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shiny.Net.HttpServer.Logging;

/// <summary>
/// Where a formatted log line goes. Implement it to send lines somewhere other than a file — a
/// rolling buffer the app's own diagnostics screen reads, or straight out over a socket.
/// </summary>
public interface IW3CLogWriter : IAsyncDisposable
{
    /// <summary>
    /// Queues one already-formatted line. Must not block: it is called on the request path.
    /// </summary>
    void Write(string line);

    /// <summary>
    /// Declares the field names, in order. Called before any line and again whenever they change,
    /// because a W3C reader takes the column meanings from the most recent <c>#Fields</c> directive.
    /// </summary>
    void SetFields(string fields);

    /// <summary>Writes everything queued. Called on shutdown, and by anything that wants the file current.</summary>
    ValueTask FlushAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Writes W3C extended log files, rolling by size and date and keeping a bounded number of them.
/// <para>
/// Nothing touches the disk on the request path. Lines go into a bounded channel and a background
/// task drains it on an interval; when the channel is full, lines are <em>dropped</em> and counted
/// rather than made to wait, because a slow or full disk must not become a slow server. The count of
/// what was lost is written into the file as a directive, so a gap is visible rather than silent.
/// </para>
/// </summary>
public sealed class W3CLogFileWriter : IW3CLogWriter
{
    readonly W3CLoggerOptions options;
    readonly ILogger logger;
    readonly Channel<string> queue;
    readonly SemaphoreSlim writeGate = new(1, 1);
    readonly CancellationTokenSource stopping = new();
    readonly Task pump;

    string? fields;
    string? currentPath;
    long currentLength;
    long dropped;
    int disposed;

    public W3CLogFileWriter(W3CLoggerOptions options, ILogger<W3CLogFileWriter>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        this.options = options;
        this.logger = logger ?? NullLogger<W3CLogFileWriter>.Instance;

        // Wait, not DropWrite: with DropWrite the channel accepts the write and silently discards
        // it, and TryWrite still answers true — so nothing could count what was lost. In Wait mode
        // TryWrite returns false the moment the queue is full, which never blocks the caller and is
        // the only way to know a line went missing.
        this.queue = Channel.CreateBounded<string>(new BoundedChannelOptions(Math.Max(16, options.MaxQueuedLines))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

        this.pump = Task.Run(this.PumpAsync, CancellationToken.None);
    }

    /// <summary>How many lines have been dropped because the queue was full. Zero on a healthy server.</summary>
    public long DroppedLines => Interlocked.Read(ref this.dropped);

    public void SetFields(string fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fields);

        if (this.fields == fields)
            return;

        var changed = this.fields is not null;
        this.fields = fields;

        // A file that has not been opened yet gets the directive in its header, which is where a
        // reader looks first. Only a change made mid-file has to be queued, so that it lands in
        // order with the lines around it — writing it both ways would declare the columns twice.
        if (changed || this.currentPath is not null)
            this.Write("#Fields: " + fields);
    }

    public void Write(string line)
    {
        if (this.disposed != 0)
            return;

        if (!this.queue.Writer.TryWrite(line))
            Interlocked.Increment(ref this.dropped);
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        // A flush that arrives after disposal is normal rather than exceptional: the server raises
        // Stopped while the app is already tearing the writer down, and throwing from there would
        // fault a thread nobody is watching.
        if (this.disposed != 0 && this.queue.Reader.Count == 0)
            return;

        try
        {
            await this.writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
        {
            return;
        }

        try
        {
            // The queue is drained *inside* the gate. Draining outside it would let two flushes —
            // the timer's and the one the server fires on shutdown — each take half the lines and
            // then race to append, which reorders a log whose whole value is being in order.
            var batch = new List<string>();

            while (this.queue.Reader.TryRead(out var line))
                batch.Add(line);

            if (batch.Count == 0)
                return;

            await this.AppendAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                this.writeGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    async Task PumpAsync()
    {
        var token = this.stopping.Token;

        try
        {
            using var timer = new PeriodicTimer(this.options.FlushInterval);

            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                await this.FlushAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down. The final flush happens in DisposeAsync.
        }
        catch (Exception ex)
        {
            // The pump is nobody's task to await, so a fault here would be an unobserved exception
            // that takes the process with it.
            this.logger.LogError(ex, "The W3C log writer stopped");
        }
    }

    /// <summary>Writes one batch. The caller holds the write gate.</summary>
    async Task AppendAsync(List<string> batch, CancellationToken cancellationToken)
    {
        try
        {
            var builder = new StringBuilder();

            if (Interlocked.Exchange(ref this.dropped, 0) is > 0 and var lost)
            {
                builder
                    .Append("#Remark: ")
                    .Append(lost.ToString(CultureInfo.InvariantCulture))
                    .Append(" line(s) dropped; the log queue was full")
                    .Append('\n');
            }

            foreach (var line in batch)
                builder.Append(line).Append('\n');

            var payload = builder.ToString();
            var bytes = Encoding.UTF8.GetByteCount(payload);

            var path = this.EnsureFile(bytes);
            await File.AppendAllTextAsync(path, payload, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

            this.currentLength += bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A full disk, a revoked permission, a directory that went away underneath us. Logging
            // is not worth failing requests over, so this is reported and the lines are lost.
            this.logger.LogWarning(ex, "Could not write the W3C log file");
        }
    }

    /// <summary>Returns the file to append to, rolling and pruning when this batch would not fit.</summary>
    string EnsureFile(int incoming)
    {
        if (this.currentPath is { } existing && this.currentLength + incoming <= this.options.FileSizeLimit && File.Exists(existing))
            return existing;

        Directory.CreateDirectory(this.options.LogDirectory);

        var date = DateTimeOffset.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var prefix = this.options.FileNamePrefix + date + ".";

        // The next index is one past the highest that exists, never the first gap. Reusing a low
        // index after pruning removed it would create a file that the very next prune deletes as
        // the oldest — while it is the one being written to.
        var highest = -1;

        foreach (var file in Directory.GetFiles(this.options.LogDirectory, prefix + "*.txt"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var suffix = name[prefix.Length..];

            if (int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index > highest)
                highest = index;
        }

        var candidate = highest >= 0
            ? Path.Combine(this.options.LogDirectory, $"{prefix}{highest:0000}.txt")
            : null;

        var length = candidate is not null && File.Exists(candidate) ? new FileInfo(candidate).Length : 0;

        if (candidate is null || (length > 0 && length + incoming > this.options.FileSizeLimit))
        {
            candidate = Path.Combine(this.options.LogDirectory, $"{prefix}{highest + 1:0000}.txt");
            length = 0;
        }

        if (length == 0)
            this.WriteHeader(candidate);

        this.currentPath = candidate;
        this.currentLength = length;

        this.Prune();

        return candidate;
    }

    /// <summary>
    /// The directives that make a file readable on its own. A W3C reader needs <c>#Fields</c> in
    /// every file, not only in the first one a process wrote.
    /// </summary>
    void WriteHeader(string path)
    {
        var header = new StringBuilder()
            .Append("#Version: 1.0\n")
            .Append("#Software: Shiny.Net.HttpServer\n")
            .Append("#Start-Date: ")
            .Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            .Append('\n');

        if (this.fields is { Length: > 0 } names)
            header.Append("#Fields: ").Append(names).Append('\n');

        File.WriteAllText(path, header.ToString(), Encoding.UTF8);
    }

    void Prune()
    {
        if (this.options.RetainedFileCountLimit <= 0)
            return;

        try
        {
            var files = Directory
                .GetFiles(this.options.LogDirectory, this.options.FileNamePrefix + "*.txt")
                .OrderByDescending(x => x, StringComparer.Ordinal)
                .Skip(this.options.RetainedFileCountLimit)
                .ToList();

            foreach (var file in files)
                File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.logger.LogDebug(ex, "Could not prune old W3C log files");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        this.queue.Writer.TryComplete();

        await this.stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            await this.pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        // Whatever is still queued belongs in the file: a shutdown is exactly when the last few
        // lines matter most.
        await this.FlushAsync(CancellationToken.None).ConfigureAwait(false);

        this.stopping.Dispose();
        this.writeGate.Dispose();
    }
}

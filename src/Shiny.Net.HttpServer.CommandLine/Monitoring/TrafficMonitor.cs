using System.Collections.Concurrent;

namespace Shiny.Net.HttpServer.CommandLine.Monitoring;


/// <summary>
/// Watches every request the server answers: what is in flight right now, and a bounded log of what
/// has finished. The dashboard reads it; nothing in the pipeline depends on it.
/// </summary>
/// <remarks>
/// <para>
/// Installed first, ahead of authentication, so a refused login is in the log alongside everything
/// else - a 401 is exactly what someone watching a server wants to see. Connections arriving through
/// the tunnel are served by the same pipeline, so they are in it too, marked as such.
/// </para>
/// <para>
/// Outlives the server it watches. Applying new settings builds a new server, and the log carries on
/// across it rather than starting again from nothing.
/// </para>
/// </remarks>
public sealed class TrafficMonitor(int capacity = 1000) : IHttpMiddleware
{
    readonly ConcurrentDictionary<long, TrafficEntry> active = new();
    readonly Queue<TrafficEntry> completed = new();
    readonly Lock gate = new();

    long nextId;
    long version;
    long requests;
    long bytesIn;
    long bytesOut;

    /// <summary>The most finished entries kept. The oldest go first.</summary>
    public int Capacity { get; } = capacity;

    /// <summary>Moves whenever something starts, finishes or is cleared - a cheap "has anything changed".</summary>
    public long Version => Interlocked.Read(ref this.version);

    public long TotalRequests => Interlocked.Read(ref this.requests);

    /// <summary>Body bytes received by requests that have finished.</summary>
    public long TotalBytesIn => Interlocked.Read(ref this.bytesIn);

    /// <summary>Body bytes sent by responses that have finished.</summary>
    public long TotalBytesOut => Interlocked.Read(ref this.bytesOut);

    public int ActiveCount => this.active.Count;


    /// <summary>What is running now, oldest first.</summary>
    public IReadOnlyList<TrafficEntry> GetActive()
        => this.active.Values.OrderBy(x => x.Id).ToArray();


    /// <summary>What has finished, newest first.</summary>
    public IReadOnlyList<TrafficEntry> GetCompleted()
    {
        lock (this.gate)
        {
            var list = this.completed.ToArray();
            Array.Reverse(list);
            return list;
        }
    }


    /// <summary>
    /// Empties the log and zeroes the totals. What is still in flight stays on screen, and lands in
    /// the freshly emptied log when it finishes.
    /// </summary>
    public void Clear()
    {
        lock (this.gate)
        {
            this.completed.Clear();
            Interlocked.Exchange(ref this.requests, 0);
            Interlocked.Exchange(ref this.bytesIn, 0);
            Interlocked.Exchange(ref this.bytesOut, 0);
        }
        Interlocked.Increment(ref this.version);
    }


    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var entry = new TrafficEntry(Interlocked.Increment(ref this.nextId), context);
        this.active[entry.Id] = entry;
        Interlocked.Increment(ref this.version);

        if (context.Request.HasBody)
            context.Request.Body = new CountingReadStream(context.Request.Body, entry);

        context.Response.Bind(new CountingBodyControl(context.Response.BodyControl, context.Response, entry));

        Exception? failure = null;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            entry.Complete(context, failure);
            this.active.TryRemove(entry.Id, out _);

            lock (this.gate)
            {
                this.completed.Enqueue(entry);
                while (this.completed.Count > this.Capacity)
                    this.completed.Dequeue();

                Interlocked.Increment(ref this.requests);
                Interlocked.Add(ref this.bytesIn, entry.BytesIn);
                Interlocked.Add(ref this.bytesOut, entry.BytesOut);
            }
            Interlocked.Increment(ref this.version);
        }
    }
}

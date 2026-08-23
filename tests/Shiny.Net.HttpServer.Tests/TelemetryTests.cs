using System.Diagnostics;
using System.Diagnostics.Metrics;
using Shiny.Net.HttpServer.Telemetry;

namespace Shiny.Net.HttpServer.Tests;

public class TelemetryTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Records_a_duration_measurement_tagged_with_the_route()
    {
        using var measurements = new MeasurementCollector("http.server.request.duration");

        await using var test = await TestServer.StartAsync(server =>
        {
            server.UseTelemetry();
            server.MapGet("/users/{id:int}", ctx => ctx.Response.WriteTextAsync("ok", cancellationToken: ctx.RequestAborted));
        });

        await test.Client.GetAsync("/users/42", Token);

        var recorded = measurements.Single();
        Assert.True(recorded.Value >= 0);
        Assert.Equal("GET", recorded.Tags["http.request.method"]);
        Assert.Equal("/users/{id:int}", recorded.Tags["http.route"]);
        Assert.Equal(200, recorded.Tags["http.response.status_code"]);
        Assert.Equal("1.1", recorded.Tags["network.protocol.version"]);
    }

    /// <summary>A 5xx is the server's fault, so it carries error.type. A 4xx is the caller's and does not.</summary>
    [Fact]
    public async Task Tags_server_errors_only()
    {
        using var measurements = new MeasurementCollector("http.server.request.duration");

        await using var test = await TestServer.StartAsync(server =>
        {
            server.UseTelemetry();
            server.MapGet("/missing", ctx =>
            {
                ctx.Response.StatusCode = 404;
                return default;
            });
            server.MapGet("/broken", ctx =>
            {
                ctx.Response.StatusCode = 503;
                return default;
            });
        });

        await test.Client.GetAsync("/missing", Token);
        await test.Client.GetAsync("/broken", Token);

        measurements.WaitFor(2);

        Assert.DoesNotContain("error.type", measurements[0].Tags.Keys);
        Assert.Equal("503", measurements[1].Tags["error.type"]);
    }

    /// <summary>An unrecognised method is reported as _OTHER, so a hostile client cannot mint attribute values.</summary>
    [Fact]
    public async Task Bounds_the_method_attribute()
    {
        using var measurements = new MeasurementCollector("http.server.request.duration");

        await using var test = await TestServer.StartAsync(server => server.UseTelemetry());
        await test.SendRawAsync("FROBNICATE / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");

        Assert.Equal("_OTHER", measurements.Single().Tags["http.request.method"]);
    }

    [Fact]
    public async Task Starts_a_span_named_for_the_route()
    {
        using var spans = new SpanCollector();

        await using var test = await TestServer.StartAsync(server =>
        {
            server.UseTelemetry();
            server.MapGet("/orders/{id}", ctx => ctx.Response.WriteTextAsync("ok", cancellationToken: ctx.RequestAborted));
        });

        await test.Client.GetAsync("/orders/7", Token);

        var span = spans.Single();
        Assert.Equal("GET /orders/{id}", span.DisplayName);
        Assert.Equal(ActivityKind.Server, span.Kind);
        Assert.Equal(200, span.GetTagItem("http.response.status_code"));
    }

    [Fact]
    public async Task Continues_the_caller_trace()
    {
        using var spans = new SpanCollector();

        await using var test = await TestServer.StartAsync(server =>
        {
            server.UseTelemetry();
            server.MapGet("/ping", ctx => ctx.Response.WriteTextAsync("pong", cancellationToken: ctx.RequestAborted));
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/ping");
        request.Headers.Add("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        await test.Client.SendAsync(request, Token);

        var span = spans.Single();
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", span.TraceId.ToHexString());
        Assert.Equal("b7ad6b7169203331", span.ParentSpanId.ToHexString());
    }

    /// <summary>A caller-chosen trace id is a caller-chosen trace id. Servers on a tunnel turn this off.</summary>
    [Fact]
    public async Task Can_refuse_an_incoming_trace()
    {
        using var spans = new SpanCollector();

        await using var test = await TestServer.StartAsync(server =>
        {
            server.UseTelemetry(o => o.ContinueIncomingTrace = false);
            server.MapGet("/ping", ctx => ctx.Response.WriteTextAsync("pong", cancellationToken: ctx.RequestAborted));
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/ping");
        request.Headers.Add("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        await test.Client.SendAsync(request, Token);

        Assert.NotEqual("0af7651916cd43dd8448eb211c80319c", spans.Single().TraceId.ToHexString());
    }

    [Fact]
    public async Task A_request_the_filter_rejects_is_not_recorded()
    {
        using var measurements = new MeasurementCollector("http.server.request.duration");

        await using var test = await TestServer.StartAsync(server =>
        {
            server.UseTelemetry(o => o.ShouldRecord = ctx => ctx.Request.Path != "/health");
            server.MapGet("/health", ctx => ctx.Response.WriteTextAsync("ok", cancellationToken: ctx.RequestAborted));
            server.MapGet("/work", ctx => ctx.Response.WriteTextAsync("ok", cancellationToken: ctx.RequestAborted));
        });

        await test.Client.GetAsync("/health", Token);
        await test.Client.GetAsync("/work", Token);

        measurements.WaitFor(1);

        Assert.Equal("/work", Assert.Single(measurements).Tags["http.route"]);
    }

    [Fact]
    public async Task Reports_the_connection_count()
    {
        using var collector = new MeasurementCollector("http.server.active_connections");

        await using var test = await TestServer.StartAsync(server =>
        {
            server.UseTelemetry();
            server.MapGet("/ping", ctx => ctx.Response.WriteTextAsync("pong", cancellationToken: ctx.RequestAborted));
        });

        await test.Client.GetAsync("/ping", Token);
        collector.RecordObservable();

        // The keep-alive connection the client is holding open.
        Assert.Contains(collector, x => x.Value >= 1);
    }

    sealed class SpanCollector : IDisposable
    {
        readonly List<Activity> spans = [];
        readonly ActivityListener listener;

        public SpanCollector()
        {
            this.listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == HttpServerTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    lock (this.spans)
                        this.spans.Add(activity);
                }
            };

            ActivitySource.AddActivityListener(this.listener);
        }

        /// <summary>
        /// Polls rather than reading once: the span is recorded when the middleware unwinds, which
        /// can be after the client already has its response.
        /// </summary>
        public Activity Single()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);

            while (DateTime.UtcNow < deadline)
            {
                lock (this.spans)
                {
                    if (this.spans.Count > 0)
                        return Assert.Single(this.spans);
                }

                Thread.Sleep(10);
            }

            lock (this.spans)
                return Assert.Single(this.spans);
        }

        public void Dispose() => this.listener.Dispose();
    }

    sealed class MeasurementCollector : List<(double Value, IReadOnlyDictionary<string, object?> Tags)>, IDisposable
    {
        readonly MeterListener listener;

        public MeasurementCollector(string instrumentName)
        {
            this.listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == HttpServerTelemetry.MeterName && instrument.Name == instrumentName)
                        l.EnableMeasurementEvents(instrument);
                }
            };

            this.listener.SetMeasurementEventCallback<double>((_, value, tags, _) => this.Record(value, tags));
            this.listener.SetMeasurementEventCallback<long>((_, value, tags, _) => this.Record(value, tags));
            this.listener.Start();
        }

        /// <summary>Pulls the observable instruments, which only report when something asks them to.</summary>
        public void RecordObservable() => this.listener.RecordObservableInstruments();

        void Record(double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copy = new Dictionary<string, object?>(tags.Length);
            foreach (var tag in tags)
                copy[tag.Key] = tag.Value;

            lock (this)
                this.Add((value, copy));
        }

        public (double Value, IReadOnlyDictionary<string, object?> Tags) Single()
        {
            this.WaitFor(1);

            lock (this)
                return Assert.Single(this);
        }

        /// <summary>Waits for a number of measurements — they land as the middleware unwinds, not as the client returns.</summary>
        public void WaitFor(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);

            while (DateTime.UtcNow < deadline)
            {
                lock (this)
                {
                    if (this.Count >= count)
                        return;
                }

                Thread.Sleep(10);
            }
        }

        public void Dispose() => this.listener.Dispose();
    }
}

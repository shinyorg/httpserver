using System.Net;
using Shiny.Net.HttpServer.Logging;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The W3C log, checked as a file a log analyser has to be able to read: the directives that make it
/// self-describing, one line per request in the declared column order, and a value that never breaks
/// the column layout however hostile the header was.
/// </summary>
public class W3CLoggingTests : IDisposable
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "shiny-w3c-" + Guid.NewGuid().ToString("n")
    );

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this.directory))
                Directory.Delete(this.directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }

        GC.SuppressFinalize(this);
    }

    W3CLoggerOptions Options(W3CLoggingFields? fields = null) => new()
    {
        LogDirectory = this.directory,
        Fields = fields ?? W3CLoggingFields.Default,
        FlushInterval = TimeSpan.FromMilliseconds(50)
    };

    async Task<string> LogAsync(W3CLoggerOptions options, Func<TestServer, Task> exercise)
    {
        await using var writer = new W3CLogFileWriter(options);

        await using (var test = await TestServer.StartAsync(server =>
        {
            server.UseW3CLogging(options, writer);
            server.MapGet("/hello", ctx => ctx.Response.WriteTextAsync("hi", cancellationToken: ctx.RequestAborted));
            server.MapGet("/users/{id:int}", ctx => ctx.Response.WriteTextAsync("user", cancellationToken: ctx.RequestAborted));
            server.MapPost("/upload", async ctx =>
            {
                using var reader = new StreamReader(ctx.Request.Body);
                await reader.ReadToEndAsync(ctx.RequestAborted);

                await ctx.Response.WriteTextAsync("ok", cancellationToken: ctx.RequestAborted);
            });
        }))
        {
            await exercise(test);
        }

        await writer.FlushAsync(Token);

        var file = Directory.GetFiles(this.directory, "*.txt").Single();

        return await File.ReadAllTextAsync(file, Token);
    }

    [Fact]
    public async Task Writes_the_directives_that_make_the_file_self_describing()
    {
        var log = await LogAsync(this.Options(), async test => await test.Client.GetStringAsync("/hello", Token));

        Assert.StartsWith("#Version: 1.0", log);
        Assert.Contains("#Software: Shiny.Net.HttpServer", log);
        Assert.Contains("#Start-Date: ", log);
        Assert.Contains("#Fields: date time c-ip cs-username s-ip s-port cs-method cs-uri-stem cs-uri-query sc-status", log);
    }

    [Fact]
    public async Task Writes_one_line_per_request_in_the_declared_order()
    {
        var options = this.Options(W3CLoggingFields.Method | W3CLoggingFields.UriStem | W3CLoggingFields.ProtocolStatus);

        var log = await LogAsync(options, async test =>
        {
            await test.Client.GetStringAsync("/hello", Token);
            await test.Client.GetAsync("/missing", Token);
        });

        var lines = Lines(log);

        Assert.Equal("#Fields: cs-method cs-uri-stem sc-status", lines[0]);
        Assert.Equal("GET /hello 200", lines[1]);
        Assert.Equal("GET /missing 404", lines[2]);
    }

    [Fact]
    public async Task An_absent_value_is_a_dash()
    {
        var options = this.Options(W3CLoggingFields.UriQuery | W3CLoggingFields.UserName | W3CLoggingFields.Referer);

        var log = await LogAsync(options, async test => await test.Client.GetStringAsync("/hello", Token));

        Assert.Equal("- - -", Lines(log)[1]);
    }

    [Fact]
    public async Task The_query_is_logged_without_its_question_mark()
    {
        var options = this.Options(W3CLoggingFields.UriStem | W3CLoggingFields.UriQuery);

        var log = await LogAsync(options, async test => await test.Client.GetStringAsync("/hello?a=1&b=2", Token));

        Assert.Equal("/hello a=1&b=2", Lines(log)[1]);
    }

    /// <summary>A space inside a value would silently become a new column, so it never survives.</summary>
    [Fact]
    public async Task A_value_containing_spaces_cannot_break_the_columns()
    {
        var options = this.Options(W3CLoggingFields.Method | W3CLoggingFields.UserAgent);

        var log = await LogAsync(options, async test =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/hello");
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (a phone) Safari/605");

            await test.Client.SendAsync(request, Token);
        });

        var line = Lines(log)[1];

        Assert.Equal("GET Mozilla/5.0+(a+phone)+Safari/605", line);
        Assert.Equal(2, line.Split(' ').Length);
    }

    [Fact]
    public async Task Logs_the_route_template_rather_than_the_path_when_asked()
    {
        var options = this.Options(W3CLoggingFields.UriStem | W3CLoggingFields.Route);

        var log = await LogAsync(options, async test => await test.Client.GetStringAsync("/users/42", Token));

        Assert.Equal("/users/42 /users/{id:int}", Lines(log)[1]);
    }

    [Fact]
    public async Task Logs_the_bytes_in_each_direction()
    {
        var options = this.Options(W3CLoggingFields.BytesReceived | W3CLoggingFields.BytesSent);

        var log = await LogAsync(options, async test =>
            await test.Client.PostAsync("/upload", new StringContent("12345"), Token));

        // sc-bytes then cs-bytes, the order IIS writes them in: "ok" out, "12345" in.
        Assert.Equal("#Fields: sc-bytes cs-bytes", Lines(log)[0]);
        Assert.Equal("2 5", Lines(log)[1]);
    }

    [Fact]
    public async Task Additional_request_headers_become_their_own_columns()
    {
        var options = this.Options(W3CLoggingFields.Method);
        options.AdditionalRequestHeaders.Add("X-Tenant");

        var log = await LogAsync(options, async test =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/hello");
            request.Headers.Add("X-Tenant", "acme");

            await test.Client.SendAsync(request, Token);
        });

        Assert.Equal("#Fields: cs-method cs(X-Tenant)", Lines(log)[0]);
        Assert.Equal("GET acme", Lines(log)[1]);
    }

    [Fact]
    public async Task A_filtered_request_is_not_logged()
    {
        var options = this.Options(W3CLoggingFields.UriStem);
        options.ShouldLog = ctx => ctx.Request.Path != "/hello";

        var log = await LogAsync(options, async test =>
        {
            await test.Client.GetStringAsync("/hello", Token);
            await test.Client.GetAsync("/missing", Token);
        });

        var lines = Lines(log);

        Assert.Equal(2, lines.Count);
        Assert.Equal("/missing", lines[1]);
    }

    /// <summary>The cookie carries session tokens, and a log file gets copied around.</summary>
    [Fact]
    public void The_cookie_header_is_not_in_the_default_field_set()
    {
        Assert.False(W3CLoggingFields.Default.HasFlag(W3CLoggingFields.Cookie));
        Assert.True(W3CLoggingFields.All.HasFlag(W3CLoggingFields.Cookie));
    }

    [Fact]
    public async Task Rolls_to_a_new_file_past_the_size_limit_and_keeps_only_what_it_was_told_to()
    {
        var options = this.Options(W3CLoggingFields.UriStem);
        options.FileSizeLimit = 200;
        options.RetainedFileCountLimit = 2;

        await using (var writer = new W3CLogFileWriter(options))
        {
            writer.SetFields("cs-uri-stem");

            for (var i = 0; i < 60; i++)
            {
                writer.Write("/a-path-long-enough-to-fill-a-small-file/" + i);
                await writer.FlushAsync(Token);
            }
        }

        var files = Directory.GetFiles(this.directory, "*.txt");

        Assert.Equal(2, files.Length);

        // Every file carries the directives, so each one is readable on its own.
        foreach (var file in files)
            Assert.Contains("#Fields: cs-uri-stem", await File.ReadAllTextAsync(file, Token));
    }

    /// <summary>A request must never wait on a busy disk, so a full queue drops lines and says so.</summary>
    [Fact]
    public async Task A_full_queue_drops_lines_and_records_that_it_did()
    {
        var options = this.Options(W3CLoggingFields.UriStem);
        options.MaxQueuedLines = 16;
        options.FlushInterval = TimeSpan.FromMinutes(5);      // nothing drains it behind our back

        await using var writer = new W3CLogFileWriter(options);
        writer.SetFields("cs-uri-stem");

        for (var i = 0; i < 500; i++)
            writer.Write("/line/" + i);

        Assert.True(writer.DroppedLines > 0);

        await writer.FlushAsync(Token);

        var log = await File.ReadAllTextAsync(Directory.GetFiles(this.directory, "*.txt").Single(), Token);

        Assert.Contains("#Remark:", log);
        Assert.Contains("line(s) dropped", log);
    }

    [Fact]
    public async Task Stopping_the_server_flushes_what_is_queued()
    {
        var options = this.Options(W3CLoggingFields.UriStem);
        options.FlushInterval = TimeSpan.FromMinutes(5);

        await using var writer = new W3CLogFileWriter(options);

        var test = await TestServer.StartAsync(server =>
        {
            server.UseW3CLogging(options, writer);
            server.MapGet("/hello", ctx => ctx.Response.WriteTextAsync("hi", cancellationToken: ctx.RequestAborted));
        });

        await test.Client.GetStringAsync("/hello", Token);
        await test.Server.StopAsync(Token);

        // StateChanged runs the flush on a task of its own, and the writer creates the directory
        // before it appends — so the directory existing says nothing about the file, and waiting on
        // it left the read to race an append that had not started. The content is the only signal
        // that the flush actually landed.
        var log = await this.WaitForLogAsync("/hello", Token);

        await test.DisposeAsync();

        Assert.Contains("/hello", log);
    }

    /// <summary>
    /// Polls the log directory until a file contains <paramref name="expected"/>, and returns the
    /// last thing it read either way — a timeout then fails on the content, which says more than a
    /// bare timeout would.
    /// </summary>
    async Task<string> WaitForLogAsync(string expected, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var log = "";

        while (true)
        {
            if (Directory.Exists(this.directory) && Directory.GetFiles(this.directory, "*.txt") is [var file, ..])
            {
                try
                {
                    log = await File.ReadAllTextAsync(file, cancellationToken);
                }
                catch (IOException)
                {
                    // The append still holds the handle. Read it on the next pass.
                }
            }

            if (log.Contains(expected, StringComparison.Ordinal) || DateTime.UtcNow >= deadline)
                return log;

            await Task.Delay(20, cancellationToken);
        }
    }

    static List<string> Lines(string log) =>
    [
        .. log
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !x.StartsWith("#Version", StringComparison.Ordinal)
                && !x.StartsWith("#Software", StringComparison.Ordinal)
                && !x.StartsWith("#Start-Date", StringComparison.Ordinal))
    ];
}

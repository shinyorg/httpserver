using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shiny.Net.HttpServer.Tunnels;

/// <summary>
/// A tunnel that runs as a separate process alongside the server.
/// <para>
/// The distinction from <c>ITunnelProvider</c> is worth being clear about. A provider <em>is</em> a
/// listener: it dials out, connections arrive over that link, and the server answers them without
/// ever binding a port. An agent is the other shape — the vendor ships a binary, the binary dials
/// out, and it forwards what arrives to a port the server is already listening on. Everything about
/// the tunnel lives in the agent; this supervises it and tells you the URL it produced.
/// </para>
/// <para>
/// That difference decides where each one runs. A provider works anywhere .NET runs, phones
/// included. An agent needs to start a process, which iOS forbids outright and Android permits only
/// in ways no app should rely on — so these are for desktop, server and the <c>shinyhttpserver</c>
/// CLI. On a phone, reach for the SSH provider or the relay.
/// </para>
/// </summary>
public interface ITunnelAgent : IAsyncDisposable
{
    /// <summary>Short name for logs — <c>cloudflared</c>, <c>ngrok</c>, <c>tailscale</c>.</summary>
    string Name { get; }

    /// <summary>The public URL, once the agent has reported one.</summary>
    string? PublicUrl { get; }

    /// <summary>True while the agent process is alive.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Starts the agent and waits for it to announce a URL. Throws when the binary is missing, the
    /// agent exits, or no URL appears before the timeout.
    /// </summary>
    Task<string> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the agent. Idempotent.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>Settings shared by every agent.</summary>
public abstract class TunnelAgentOptions
{
    /// <summary>
    /// The local port to publish. Zero means "read it from the server once it is listening", which
    /// is what the <c>StartAsync(HttpServer)</c> overloads do.
    /// </summary>
    public int Port { get; set; }

    /// <summary>The interface the agent connects back to. Loopback, and there is rarely a reason to change it.</summary>
    public string LocalHost { get; set; } = "127.0.0.1";

    /// <summary>
    /// Path to the agent binary. Null looks it up on PATH — which is where a package manager put
    /// it, and where a user who installed it by hand expects it to be found.
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>How long to wait for the agent to announce a URL before giving up.</summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Extra arguments appended to the command line, for anything this wrapper does not model.</summary>
    public IList<string> ExtraArguments { get; } = [];
}

/// <summary>
/// The shared machinery: find the binary, start it, watch its output for a URL, kill it on the way
/// out.
/// <para>
/// Every one of these agents announces its URL on stdout or stderr and nowhere else — there is no
/// API to ask, and the file each of them can write is written after the fact. So the output is
/// what gets read, and the pattern that matches it is the only per-agent parsing.
/// </para>
/// </summary>
public abstract class ProcessTunnelAgent(TunnelAgentOptions options, ILogger? logger = null) : ITunnelAgent
{
    readonly TaskCompletionSource<string> announced = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly ILogger logger = logger ?? NullLogger.Instance;

    Process? process;

    public abstract string Name { get; }

    /// <summary>The default binary name, looked up on PATH when no explicit path was given.</summary>
    protected abstract string ExecutableName { get; }

    /// <summary>The command line for the configured port.</summary>
    protected abstract IEnumerable<string> BuildArguments(int port);

    /// <summary>Pulls the public URL out of one line of agent output, or returns null.</summary>
    protected abstract string? ParseUrl(string line);

    /// <summary>The settings this agent was built with. Mutable so a caller can set the port from a started server.</summary>
    public TunnelAgentOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));

    public string? PublicUrl { get; private set; }

    public bool IsRunning => this.process is { HasExited: false };

    public async Task<string> StartAsync(CancellationToken cancellationToken = default)
    {
        if (this.PublicUrl is { } already)
            return already;

        if (this.Options.Port <= 0)
            throw new InvalidOperationException(
                $"The {this.Name} agent has no port to publish. Set options.Port, or start it from a " +
                "running server with the HttpServer overload so the bound port can be read off it."
            );

        var executable = this.Options.ExecutablePath ?? this.ExecutableName;

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in this.BuildArguments(this.Options.Port))
            startInfo.ArgumentList.Add(argument);

        foreach (var argument in this.Options.ExtraArguments)
            startInfo.ArgumentList.Add(argument);

        this.logger.LogDebug("Starting {Agent}: {File} {Arguments}", this.Name, executable, string.Join(' ', startInfo.ArgumentList));

        try
        {
            this.process = Process.Start(startInfo)
                ?? throw new TunnelAgentException(this.Name, $"'{executable}' did not start.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // By far the most common failure, and a bare "file not found" sends people looking in
            // the wrong place entirely.
            throw new TunnelAgentException(
                this.Name,
                $"'{executable}' was not found. Install the {this.Name} agent and make sure it is on PATH, " +
                $"or set ExecutablePath to its location.",
                ex
            );
        }

        this.process.OutputDataReceived += (_, e) => this.OnLine(e.Data);
        this.process.ErrorDataReceived += (_, e) => this.OnLine(e.Data);
        this.process.BeginOutputReadLine();
        this.process.BeginErrorReadLine();

        // An agent that exits during startup — bad credentials, a port already claimed — must fail
        // the wait rather than leave it running until the timeout.
        this.process.EnableRaisingEvents = true;
        this.process.Exited += (_, _) => this.announced.TrySetException(new TunnelAgentException(
            this.Name,
            $"The {this.Name} agent exited with code {this.process?.ExitCode} before announcing a URL."
        ));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(this.Options.StartTimeout);

        try
        {
            this.PublicUrl = await this.announced.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await this.StopAsync(CancellationToken.None).ConfigureAwait(false);

            throw new TunnelAgentException(
                this.Name,
                $"The {this.Name} agent did not announce a URL within {this.Options.StartTimeout.TotalSeconds:0}s."
            );
        }
        catch
        {
            await this.StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        this.logger.LogInformation("Tunnel {Agent} open at {Url}", this.Name, this.PublicUrl);

        return this.PublicUrl;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (this.process is not { } running)
            return Task.CompletedTask;

        this.process = null;

        try
        {
            if (!running.HasExited)
            {
                // The whole tree: cloudflared and ngrok both spawn helpers, and killing only the
                // parent leaves the tunnel up and the port claimed.
                running.Kill(entireProcessTree: true);
                running.WaitForExit(3000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            this.logger.LogDebug(ex, "The {Agent} agent had already gone when it was stopped", this.Name);
        }
        finally
        {
            running.Dispose();
            this.PublicUrl = null;
        }

        return Task.CompletedTask;
    }

    void OnLine(string? line)
    {
        if (line is not { Length: > 0 })
            return;

        this.logger.LogTrace("{Agent}: {Line}", this.Name, line);

        if (this.announced.Task.IsCompleted)
            return;

        if (this.ParseUrl(line) is { Length: > 0 } url)
            this.announced.TrySetResult(url.TrimEnd('/'));
    }

    public async ValueTask DisposeAsync() => await this.StopAsync(CancellationToken.None).ConfigureAwait(false);
}

/// <summary>Thrown when an agent cannot be started, or dies before it publishes anything.</summary>
public sealed class TunnelAgentException(string agent, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>Which agent failed.</summary>
    public string Agent { get; } = agent;
}

/// <summary>Starting an agent against a running server.</summary>
public static class TunnelAgentExtensions
{
    static readonly Regex PortPattern = new(@":(\d+)\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Starts the agent against the port <paramref name="server"/> is listening on.
    /// <para>
    /// The server must already be started: an agent forwards to a port, and a port that is not
    /// bound yet is a tunnel that answers 502 for as long as it takes to notice.
    /// </para>
    /// </summary>
    public static async Task<string> StartAsync(
        this ITunnelAgent agent,
        HttpServer server,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(server);

        if (agent is ProcessTunnelAgent process)
            process.Options.Port = PortOf(server);

        return await agent.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static int PortOf(HttpServer server)
    {
        if (server.ListenUrl is not { } url)
            throw new InvalidOperationException(
                "The server is not listening, so there is no port to tunnel. Call StartAsync() on it first."
            );

        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Port > 0)
            return parsed.Port;

        var match = PortPattern.Match(url);

        return match.Success
            ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"Could not work out a port from the listen URL '{url}'.");
    }
}

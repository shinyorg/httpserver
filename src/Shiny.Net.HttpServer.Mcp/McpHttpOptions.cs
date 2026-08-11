namespace Shiny.Net.HttpServer.Mcp;

/// <summary>
/// How the MCP endpoint behaves. Everything here is about the <em>transport</em>; what the server
/// actually exposes — tools, prompts, resources — is configured on the MCP SDK's
/// <c>AddMcpServer()</c> builder.
/// </summary>
public sealed class McpHttpOptions
{
    /// <summary>
    /// Runs every request against a throwaway server with no session state, refusing sessions even
    /// to a client that asks for one. What you want behind a load balancer, where the next request
    /// may not reach the process that answered this one.
    /// <para>
    /// Off by default, because the interesting case for this server is the opposite: one process on
    /// a device holding real state between calls. Note that off does not mean every request gets a
    /// session — a client that connects through <c>server/discover</c> without initializing is
    /// answered per request either way. It means a client that <em>does</em> initialize gets one.
    /// </para>
    /// <para>
    /// Sessions are also what the <c>GET</c> stream attaches to, so anything that needs the server
    /// to speak first — sampling, elicitation, roots, notifications — needs one.
    /// </para>
    /// </summary>
    public bool Stateless { get; set; }

    /// <summary>
    /// How long a session may sit untouched before it is disposed. A client that goes away without
    /// sending <c>DELETE</c> — which is most of them, most of the time — is reclaimed by this.
    /// </summary>
    public TimeSpan IdleSessionTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How often idle sessions are swept. Cheap: it walks a dictionary.
    /// </summary>
    public TimeSpan SessionSweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Ceiling on concurrent sessions. Exceeding it answers 429 rather than accepting work the
    /// device cannot hold — a phone is not a datacentre and an unbounded dictionary of live MCP
    /// servers is a memory leak with extra steps.
    /// </summary>
    public int MaxSessions { get; set; } = 32;

    /// <summary>
    /// Origins permitted to reach the endpoint from a browser. Empty means no browser origin is
    /// allowed, which is the safe default: a request carrying <c>Origin</c> is by definition coming
    /// from a page, and a server bound to localhost is otherwise a DNS-rebinding target. Native MCP
    /// clients send no <c>Origin</c> and are unaffected.
    /// <para>
    /// Values are compared case-insensitively against the raw header, so include the scheme and
    /// port exactly as the browser sends them: <c>https://inspector.example.com</c>.
    /// </para>
    /// </summary>
    public IList<string> AllowedOrigins { get; } = [];

    /// <summary>
    /// Allows any browser origin. Convenient while developing, and exactly the setting that makes
    /// a locally bound MCP server reachable from any page the user happens to have open.
    /// </summary>
    public bool AllowAnyOrigin { get; set; }

    /// <summary>
    /// Replaces the built-in origin check entirely. Return true to accept the origin.
    /// </summary>
    public Func<string, bool>? OriginValidator { get; set; }

    /// <summary>
    /// Whether <c>GET</c> opens a server-to-client SSE stream. Turning it off answers 405, which is
    /// the spec's way of saying "this server never speaks first" — clients then skip the stream
    /// instead of retrying it.
    /// </summary>
    public bool AllowServerToClientStream { get; set; } = true;

    /// <summary>
    /// Enforces the spec's requirement that a client <c>POST</c> advertises both
    /// <c>application/json</c> and <c>text/event-stream</c>. Relax it when you are poking at the
    /// endpoint with curl and cannot be bothered.
    /// </summary>
    public bool ValidateAcceptHeader { get; set; } = true;

    internal bool IsOriginAllowed(string origin)
    {
        if (this.OriginValidator is { } validator)
            return validator(origin);

        if (this.AllowAnyOrigin)
            return true;

        for (var i = 0; i < this.AllowedOrigins.Count; i++)
        {
            if (string.Equals(this.AllowedOrigins[i], origin, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

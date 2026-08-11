using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;

// The core server has a ConnectionInfo of its own, and this namespace sits inside it — so the
// nearer type wins unless the SSH one is spelled out.
using SshConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace Shiny.Net.HttpServer.Ssh;

/// <summary>
/// How to reach the SSH endpoint that will publish this server.
/// <para>
/// The device opens an ordinary outbound SSH connection and asks the server to forward a remote
/// port back down it — <c>ssh -R</c>, in library form. Nothing has to connect *to* the device, which
/// is what makes it work from behind carrier-grade NAT.
/// </para>
/// </summary>
public sealed partial class SshTunnelOptions
{
    /// <summary>The SSH host, e.g. <c>tunnel.example.com</c> or <c>localhost.run</c>.</summary>
    public string Host { get; set; } = null!;

    public int Port { get; set; } = 22;

    public string Username { get; set; } = null!;

    /// <summary>Password authentication. Prefer a key.</summary>
    public string? Password { get; set; }

    /// <summary>Path to an OpenSSH private key.</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>
    /// The private key itself, for apps that keep it somewhere other than the file system — the
    /// keychain, the Android keystore-wrapped preferences, an embedded resource.
    /// </summary>
    public byte[]? PrivateKey { get; set; }

    /// <summary>Passphrase for an encrypted private key.</summary>
    public string? PrivateKeyPassPhrase { get; set; }

    /// <summary>
    /// Authenticates with a key generated on first connect and kept in memory, when no other
    /// credential is set.
    /// <para>
    /// For the hosted tunnels that will hand a key-less client nothing: most of them do not care
    /// *which* key you present, only that you present one, and asking an app to ship or provision a
    /// key for that is ceremony with no security value. The key is generated once and reused for
    /// the life of these options, so a reconnect presents the same identity — which matters on a
    /// host like sish that derives your subdomain from the key. It does not survive a restart, so
    /// a host that keys your address off it will assign a new address each run; set
    /// <see cref="PrivateKey"/> or <see cref="PrivateKeyPath"/> from somewhere durable when the
    /// address has to stay put.
    /// </para>
    /// </summary>
    public bool UseEphemeralKey { get; set; }

    /// <summary>
    /// Interface the SSH server should bind the forwarded port on.
    /// <para>
    /// <c>localhost</c> keeps it private to the server, which is what you want when a reverse proxy
    /// on that box terminates TLS and forwards to it. <c>0.0.0.0</c> exposes it to the internet
    /// directly and requires <c>GatewayPorts</c> in the server's sshd config. Hosted tunnels
    /// (sish, localhost.run, Serveo) want <c>localhost</c> with <see cref="RemotePort"/> 80.
    /// </para>
    /// </summary>
    public string RemoteBindAddress { get; set; } = "localhost";

    /// <summary>
    /// Port to bind on the SSH server. Zero asks the server to allocate one and reports it back on
    /// <see cref="SshTunnelProvider.RemotePort"/> — which needs <c>GatewayPorts</c> or
    /// <c>AllowTcpForwarding</c> permitting it.
    /// </summary>
    public int RemotePort { get; set; }

    /// <summary>
    /// The address the world will use, when you know it up front: <c>https://device-1.example.com</c>.
    /// <para>
    /// Left null, a plain <c>http://{host}:{remotePort}</c> is reported, which is right for a direct
    /// forward and wrong the moment a reverse proxy is involved. Hosted tunnels assign a URL instead
    /// — see <see cref="CaptureUrlFromSession"/>.
    /// </para>
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>
    /// Reads the assigned URL from the server's session output.
    /// <para>
    /// How every hosted tunnel tells you where you landed: sish, localhost.run and Serveo all print
    /// the URL on the session channel once forwarding is established. Off by default, because a
    /// server you own has no such banner and opening a shell channel on it is pointless.
    /// </para>
    /// </summary>
    public bool CaptureUrlFromSession { get; set; }

    /// <summary>
    /// Pattern that picks the URL out of the session output. The default takes the first
    /// <c>https://</c> address; override it when a provider prints several.
    /// </summary>
    public Regex UrlPattern { get; set; } = DefaultUrlPattern();

    /// <summary>How long to wait for that banner before giving up and reporting no URL.</summary>
    public TimeSpan UrlCaptureTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// SHA-256 fingerprints of host keys to accept, as <c>ssh-keygen -lf</c> prints them
    /// (<c>SHA256:47DEQpj8HBSa+…</c>, with or without the prefix).
    /// <para>
    /// Leave it empty and <see cref="AcceptAnyHostKey"/> off, and connecting fails — deliberately.
    /// An unverified host key means anything between the device and the server can pose as the
    /// server, and a tunnel exists precisely to carry traffic across networks you do not control.
    /// </para>
    /// </summary>
    public IList<string> HostKeyFingerprints { get; } = [];

    /// <summary>
    /// Accepts whatever host key the server offers.
    /// <para>
    /// Trust-on-first-use without the first use. Reasonable while you are finding the fingerprint
    /// to pin; not reasonable in something you ship.
    /// </para>
    /// </summary>
    public bool AcceptAnyHostKey { get; set; }

    /// <summary>How long to wait for the SSH handshake and authentication.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often to send a keep-alive.
    /// <para>
    /// Something between the device and the server will drop an idle NAT mapping — carriers are the
    /// worst offenders, at a minute or two. Long enough to be silent, short enough to keep the
    /// mapping: 30 seconds is a reasonable compromise on cellular.
    /// </para>
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Reconnects after the SSH connection drops. On by default — a phone changes networks, and the
    /// tunnel should come back on its own rather than needing the app restarted.
    /// </summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>First retry delay; doubles up to <see cref="MaxReconnectDelay"/>.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Loopback port the tunnel listens on for forwarded connections. Zero (the default) takes an
    /// ephemeral one, which is what you want — nothing else needs to find it.
    /// </summary>
    public int LocalPort { get; set; }

    /// <summary>Validates the options and builds the SSH connection info.</summary>
    internal SshConnectionInfo CreateConnectionInfo()
    {
        if (string.IsNullOrWhiteSpace(this.Host))
            throw new InvalidOperationException($"{nameof(this.Host)} is required.");

        if (string.IsNullOrWhiteSpace(this.Username))
            throw new InvalidOperationException($"{nameof(this.Username)} is required.");

        if (!this.AcceptAnyHostKey && this.HostKeyFingerprints.Count == 0)
            throw new InvalidOperationException(
                $"No host key to verify against. Pin one in {nameof(this.HostKeyFingerprints)} " +
                $"(ssh-keyscan -p {this.Port} {this.Host} | ssh-keygen -lf -), or set " +
                $"{nameof(this.AcceptAnyHostKey)} if you accept that anything on the path can pose as the server."
            );

        var methods = new List<AuthenticationMethod>();

        if (this.PrivateKey is { Length: > 0 } keyBytes)
        {
            using var stream = new MemoryStream(keyBytes);
            methods.Add(new PrivateKeyAuthenticationMethod(this.Username, LoadKey(stream, this.PrivateKeyPassPhrase)));
        }
        else if (this.PrivateKeyPath is { Length: > 0 } path)
        {
            methods.Add(new PrivateKeyAuthenticationMethod(this.Username, LoadKey(path, this.PrivateKeyPassPhrase)));
        }

        if (this.Password is not null)
            methods.Add(new PasswordAuthenticationMethod(this.Username, this.Password));

        // Nothing was supplied. A generated key satisfies the hosts that want any key at all, and
        // "none" covers the ones that authenticate nobody — offered in that order because a server
        // that wants a key answers "none" with a permission denied, and the reverse costs nothing.
        if (methods.Count == 0 && this.UseEphemeralKey)
            methods.Add(new PrivateKeyAuthenticationMethod(this.Username, this.EphemeralKey()));

        if (methods.Count == 0)
            methods.Add(new NoneAuthenticationMethod(this.Username));

        return new SshConnectionInfo(this.Host, this.Port, this.Username, [.. methods])
        {
            Timeout = this.ConnectTimeout
        };
    }

    /// <summary>
    /// The generated key, made once and kept.
    /// <para>
    /// RSA rather than Ed25519 only because .NET can export it in a PEM SSH.NET parses without
    /// hand-rolling the OpenSSH key format. Generation costs a couple of hundred milliseconds on a
    /// phone, and it happens on the connect path rather than at startup so an app that never opens
    /// a tunnel never pays it.
    /// </para>
    /// </summary>
    PrivateKeyFile EphemeralKey()
    {
        if (this.ephemeralKey is not null)
            return this.ephemeralKey;

        using var rsa = RSA.Create(2048);
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(rsa.ExportRSAPrivateKeyPem()));

        return this.ephemeralKey = new PrivateKeyFile(stream);
    }

    PrivateKeyFile? ephemeralKey;

    static PrivateKeyFile LoadKey(string path, string? passPhrase) =>
        passPhrase is { Length: > 0 } ? new PrivateKeyFile(path, passPhrase) : new PrivateKeyFile(path);

    static PrivateKeyFile LoadKey(Stream stream, string? passPhrase) =>
        passPhrase is { Length: > 0 } ? new PrivateKeyFile(stream, passPhrase) : new PrivateKeyFile(stream);

    [GeneratedRegex(@"https://[^\s""'<>\)]+", RegexOptions.IgnoreCase)]
    private static partial Regex DefaultUrlPattern();
}

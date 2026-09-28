using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shiny.Net.HttpServer.Acme;

/// <summary>
/// Where the ACME account key and issued certificates are kept between runs.
/// <para>
/// Persistence is not optional in practice. A server that forgets its certificate on restart orders a
/// new one on every start, and Let's Encrypt allows five duplicate certificates per week — a crash loop
/// exhausts that in minutes and leaves the site without TLS for days. And a forgotten account key
/// means a new account per start, which also has a rate limit.
/// </para>
/// <para>
/// Everything is keyed by the CA's directory URL, so staging and production never mix. Both the
/// account and the certificate contain private keys: an implementation that puts them anywhere
/// shared (a database, a secret manager) owns protecting them there.
/// </para>
/// </summary>
public interface IAcmeCertificateStore
{
    /// <summary>The account for this CA, or null when there is none yet.</summary>
    Task<AcmeAccountData?> LoadAccountAsync(string directoryUrl, CancellationToken cancellationToken);

    /// <summary>Saves the account for this CA, replacing any previous one.</summary>
    Task SaveAccountAsync(string directoryUrl, AcmeAccountData account, CancellationToken cancellationToken);

    /// <summary>
    /// The certificate saved under <paramref name="name"/> — a PKCS#12 blob holding the certificate, its
    /// private key and its chain — or null.
    /// </summary>
    Task<byte[]?> LoadCertificateAsync(string directoryUrl, string name, CancellationToken cancellationToken);

    /// <summary>Saves a PKCS#12 blob under <paramref name="name"/>, replacing any previous one.</summary>
    Task SaveCertificateAsync(string directoryUrl, string name, byte[] pkcs12, CancellationToken cancellationToken);
}

/// <summary>An ACME account: the key that signs every request, and the URL the CA knows it by.</summary>
public sealed class AcmeAccountData
{
    public AcmeAccountData(byte[] privateKey, string? location)
    {
        ArgumentNullException.ThrowIfNull(privateKey);

        this.PrivateKey = privateKey;
        this.Location = location;
    }

    /// <summary>The account's ECDSA P-256 private key, PKCS#8 DER.</summary>
    public byte[] PrivateKey { get; }

    /// <summary>The account URL (the JWS <c>kid</c>), or null when the account has not been registered yet.</summary>
    public string? Location { get; }
}

/// <summary>
/// The default store: files in a directory.
/// <code>
/// {root}/{ca-host}-{hash}/account.json      account key (PKCS#8) and account URL
/// {root}/{ca-host}-{hash}/{name}.pfx        certificate, key and chain (PKCS#12, no password)
/// </code>
/// <para>
/// On Linux and macOS every file is created mode <c>0600</c> and every directory <c>0700</c>, at
/// creation rather than chmod'ed afterwards, so there is no moment when a key is readable by anyone
/// else. On Windows the files inherit the directory's ACL — put the store under the service account's
/// profile (the default, LocalApplicationData, is) rather than somewhere shared. Writes go to a
/// temporary file and are renamed into place, so a crash mid-write never leaves a torn key behind.
/// </para>
/// </summary>
public sealed class DirectoryAcmeCertificateStore : IAcmeCertificateStore
{
    const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public DirectoryAcmeCertificateStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.Path = path;
    }

    /// <summary>The root directory.</summary>
    public string Path { get; }

    public async Task<AcmeAccountData?> LoadAccountAsync(string directoryUrl, CancellationToken cancellationToken)
    {
        var file = System.IO.Path.Combine(this.DirectoryFor(directoryUrl), "account.json");
        if (!File.Exists(file))
            return null;

        var json = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var key = Convert.FromBase64String(root.GetProperty("key").GetString()!);
        var location = root.TryGetProperty("location", out var value) ? value.GetString() : null;

        return new AcmeAccountData(key, location);
    }

    public Task SaveAccountAsync(string directoryUrl, AcmeAccountData account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("directory", directoryUrl);
            writer.WriteString("key", Convert.ToBase64String(account.PrivateKey));
            if (account.Location is not null)
                writer.WriteString("location", account.Location);
            writer.WriteEndObject();
        }

        return this.WriteAsync(directoryUrl, "account.json", buffer.ToArray(), cancellationToken);
    }

    public async Task<byte[]?> LoadCertificateAsync(string directoryUrl, string name, CancellationToken cancellationToken)
    {
        var file = System.IO.Path.Combine(this.DirectoryFor(directoryUrl), FileName(name));
        return File.Exists(file)
            ? await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public Task SaveCertificateAsync(string directoryUrl, string name, byte[] pkcs12, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pkcs12);
        return this.WriteAsync(directoryUrl, FileName(name), pkcs12, cancellationToken);
    }

    /// <summary>
    /// One directory per CA, named so a person can tell staging from production at a glance, with a
    /// hash of the full URL so two CAs on the same host (ZeroSSL's several directories) never collide.
    /// </summary>
    internal string DirectoryFor(string directoryUrl)
    {
        var host = Uri.TryCreate(directoryUrl, UriKind.Absolute, out var uri) ? uri.Host : "ca";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(directoryUrl)))[..8];

        return System.IO.Path.Combine(this.Path, $"{Sanitize(host)}-{hash}");
    }

    static string FileName(string name) => Sanitize(name) + ".pfx";

    static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');

        return builder.ToString();
    }

    async Task WriteAsync(string directoryUrl, string fileName, byte[] content, CancellationToken cancellationToken)
    {
        var directory = this.DirectoryFor(directoryUrl);
        CreateDirectory(this.Path);
        CreateDirectory(directory);

        var target = System.IO.Path.Combine(directory, fileName);
        var temporary = target + ".tmp";

        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None
        };

        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = OwnerOnlyFile;

        try
        {
            await using (var file = new FileStream(temporary, options))
                await file.WriteAsync(content, cancellationToken).ConfigureAwait(false);

            File.Move(temporary, target, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }

    static void CreateDirectory(string path)
    {
        if (Directory.Exists(path))
            return;

        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, OwnerOnlyDirectory);
    }
}

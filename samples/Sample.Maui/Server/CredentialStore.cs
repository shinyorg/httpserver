using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Shiny.Net.HttpServer.Security;

namespace Sample.Maui.Server;

/// <summary>
/// The one account this device accepts, changeable from the Server screen.
/// <para>
/// This is what <see cref="IBasicCredentialValidator"/> is for: the accounts are not in
/// configuration, they are in whatever the app already uses to remember things — so the validator
/// is a service with its own dependencies rather than a list handed over at startup.
/// </para>
/// <para>
/// The password is kept in <c>SecureStorage</c>, which is the keychain on Apple platforms and the
/// EncryptedSharedPreferences-backed store on Android, so it is encrypted at rest by the OS. Where
/// that is unavailable — an emulator without a keystore, a Mac Catalyst build with no keychain
/// entitlement — it falls back to <c>Preferences</c>, which is <em>not</em> encrypted; the app says
/// so rather than pretending otherwise.
/// </para>
/// </summary>
public sealed class CredentialStore : IBasicCredentialValidator
{
    const string UsernameKey = "shiny.server.username";
    const string PasswordKey = "shiny.server.password";

    readonly SemaphoreSlim gate = new(1, 1);

    string username = "admin";
    string password = string.Empty;

    public string Username => this.username;

    /// <summary>
    /// The current password, so the Server screen can show what visitors need to be told. A real
    /// product would not display it; a device you are handing a link to is exactly the case where
    /// someone has to read it out.
    /// </summary>
    public string Password => this.password;

    /// <summary>False when the password had to be kept somewhere the OS does not encrypt.</summary>
    public bool IsStoredSecurely { get; private set; } = true;

    public event EventHandler? Changed;

    /// <summary>
    /// Loads the saved credentials, generating a password on first run.
    /// <para>
    /// Generated rather than defaulted: a well-known password on something reachable from the
    /// internet is the same as no password, and the one thing worse than a prompt is a prompt
    /// everybody can answer.
    /// </para>
    /// </summary>
    public async Task LoadAsync()
    {
        this.username = Preferences.Default.Get(UsernameKey, "admin");

        var stored = await this.ReadPasswordAsync().ConfigureAwait(false);

        if (stored is { Length: > 0 })
        {
            this.password = stored;
            return;
        }

        await this.SetAsync(this.username, GeneratePassword()).ConfigureAwait(false);
    }

    /// <summary>Changes the account. Takes effect on the next request; nothing has to restart.</summary>
    public async Task SetAsync(string newUsername, string newPassword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newUsername);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPassword);

        await this.gate.WaitAsync().ConfigureAwait(false);
        try
        {
            this.username = newUsername.Trim();
            this.password = newPassword;

            Preferences.Default.Set(UsernameKey, this.username);
            await this.WritePasswordAsync(this.password).ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }

        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A fresh password, for the "regenerate" button.</summary>
    public Task RegenerateAsync() => this.SetAsync(this.username, GeneratePassword());

    public ValueTask<ClaimsPrincipal?> ValidateAsync(
        string presentedUsername,
        string presentedPassword,
        CancellationToken cancellationToken
    )
    {
        // Both compared in fixed time, and both compared regardless of whether the first matched —
        // an early return on the username would let someone learn which names exist by timing.
        var userMatches = FixedTimeEquals(presentedUsername, this.username);
        var passwordMatches = FixedTimeEquals(presentedPassword, this.password);

        if (!userMatches || !passwordMatches)
            return new ValueTask<ClaimsPrincipal?>((ClaimsPrincipal?)null);

        return new ValueTask<ClaimsPrincipal?>(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, this.username)],
            "Basic",
            ClaimTypes.Name,
            ClaimTypes.Role
        )));
    }

    /// <summary>
    /// Hashes both sides before comparing, so the comparison does not finish early on the first
    /// differing character and does not leak the length either.
    /// </summary>
    static bool FixedTimeEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(left)),
        SHA256.HashData(Encoding.UTF8.GetBytes(right))
    );

    /// <summary>
    /// A short readable password from a cryptographic source — this is a sample, so five characters
    /// is plenty; the alphabet still leaves out the characters people confuse when typing it on a phone.
    /// </summary>
    static string GeneratePassword()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        var chars = new char[5];

        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];

        return new string(chars);
    }

    async Task<string?> ReadPasswordAsync()
    {
        try
        {
            return await SecureStorage.Default.GetAsync(PasswordKey).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.IsStoredSecurely = false;

            return Preferences.Default.Get(PasswordKey, null as string);
        }
    }

    async Task WritePasswordAsync(string value)
    {
        try
        {
            await SecureStorage.Default.SetAsync(PasswordKey, value).ConfigureAwait(false);
            this.IsStoredSecurely = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.IsStoredSecurely = false;
            Preferences.Default.Set(PasswordKey, value);
        }
    }
}

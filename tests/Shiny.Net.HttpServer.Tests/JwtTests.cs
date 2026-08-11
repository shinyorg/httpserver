using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Shiny.Net.HttpServer.Jwt;

namespace Shiny.Net.HttpServer.Tests;

public class Base64UrlTests
{
    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("abc")]
    [InlineData("abcd")]
    [InlineData("hello world, this is a longer payload to push past one block")]
    public void Round_trips(string text)
    {
        var encoded = Base64Url.EncodeString(text);

        Assert.True(Base64Url.TryDecode(encoded, out var bytes));
        Assert.Equal(text, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Produces_url_safe_output_with_no_padding()
    {
        // 0xFF 0xFE 0xFD is "//79" in standard base64 — the characters that must be swapped.
        var encoded = Base64Url.Encode([0xFF, 0xFE, 0xFD]);

        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
        Assert.Equal("__79", encoded);
    }

    [Fact]
    public void Round_trips_arbitrary_bytes()
    {
        var bytes = RandomNumberGenerator.GetBytes(257);

        Assert.True(Base64Url.TryDecode(Base64Url.Encode(bytes), out var decoded));
        Assert.Equal(bytes, decoded);
    }

    [Theory]
    [InlineData("a+b")]
    [InlineData("a/b")]
    [InlineData("abcde")]
    public void Rejects_input_that_is_not_base64url(string value)
        => Assert.False(Base64Url.TryDecode(value, out _));
}

public class JwtRoundTripTests
{
    static readonly byte[] Secret = Encoding.UTF8.GetBytes("a-32-byte-secret-for-testing!!!!");

    static JwtValidationParameters Parameters(JwtSigningKey key) => new()
    {
        ValidIssuers = { "shiny" },
        ValidAudiences = { "shiny-app" },
        SigningKeys = { key }
    };

    static JwtTokenDescriptor Descriptor() => new()
    {
        Issuer = "shiny",
        Subject = "user-1",
        Audiences = { "shiny-app" },
        Lifetime = TimeSpan.FromMinutes(30)
    };

    [Theory]
    [InlineData(JwtAlgorithm.HS256)]
    [InlineData(JwtAlgorithm.HS384)]
    [InlineData(JwtAlgorithm.HS512)]
    public void Round_trips_an_hmac_token(JwtAlgorithm algorithm)
    {
        using var key = JwtSigningKey.CreateSecret(algorithm);
        var token = new JwtTokenGenerator(key).Create(Descriptor());
        var result = new JwtTokenValidator(Parameters(key)).Validate(token);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal("user-1", result.Principal.FindFirst(JwtClaimNames.Subject)?.Value);
    }

    [Theory]
    [InlineData(JwtAlgorithm.RS256)]
    [InlineData(JwtAlgorithm.RS384)]
    [InlineData(JwtAlgorithm.RS512)]
    public void Round_trips_an_rsa_token(JwtAlgorithm algorithm)
    {
        using var rsa = RSA.Create(2048);
        using var key = JwtSigningKey.FromRsa(rsa, algorithm);

        var token = new JwtTokenGenerator(key).Create(Descriptor());
        var result = new JwtTokenValidator(Parameters(key)).Validate(token);

        Assert.True(result.IsValid, result.Error);
    }

    [Theory]
    [InlineData(JwtAlgorithm.ES256)]
    [InlineData(JwtAlgorithm.ES384)]
    [InlineData(JwtAlgorithm.ES512)]
    public void Round_trips_an_ecdsa_token(JwtAlgorithm algorithm)
    {
        var curve = algorithm switch
        {
            JwtAlgorithm.ES256 => ECCurve.NamedCurves.nistP256,
            JwtAlgorithm.ES384 => ECCurve.NamedCurves.nistP384,
            _ => ECCurve.NamedCurves.nistP521
        };

        using var ecdsa = ECDsa.Create(curve);
        using var key = JwtSigningKey.FromEcdsa(ecdsa, algorithm);

        var token = new JwtTokenGenerator(key).Create(Descriptor());
        var result = new JwtTokenValidator(Parameters(key)).Validate(token);

        Assert.True(result.IsValid, result.Error);
    }

    [Fact]
    public void Produces_three_base64url_parts()
    {
        using var key = JwtSigningKey.FromSecret(Secret);
        var token = new JwtTokenGenerator(key).Create(Descriptor());

        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.All(parts, p => Assert.True(Base64Url.TryDecode(p, out _)));

        var header = Encoding.UTF8.GetString(Base64Url.Decode(parts[0]));
        Assert.Contains("\"alg\":\"HS256\"", header);
        Assert.Contains("\"typ\":\"JWT\"", header);
    }

    [Fact]
    public void Carries_roles_and_custom_claims()
    {
        using var key = JwtSigningKey.FromSecret(Secret);

        var descriptor = Descriptor();
        descriptor.AddRoles("admin", "auditor");
        descriptor.AddClaim("tenant", "acme");
        descriptor.AddClaim(JwtClaimNames.Name, "Ada");

        var token = new JwtTokenGenerator(key).Create(descriptor);
        var result = new JwtTokenValidator(Parameters(key)).Validate(token);

        Assert.True(result.IsValid, result.Error);
        Assert.True(result.Principal.IsInRole("admin"));
        Assert.True(result.Principal.IsInRole("auditor"));
        Assert.False(result.Principal.IsInRole("nobody"));
        Assert.Equal("acme", result.Principal.FindFirst("tenant")?.Value);
        Assert.Equal("Ada", result.Principal.Identity?.Name);
        Assert.True(result.Principal.Identity?.IsAuthenticated);
    }

    [Fact]
    public void Writes_a_single_audience_as_a_string_and_several_as_an_array()
    {
        using var key = JwtSigningKey.FromSecret(Secret);
        var generator = new JwtTokenGenerator(key);

        var one = Encoding.UTF8.GetString(Base64Url.Decode(generator.Create(Descriptor()).Split('.')[1]));
        Assert.Contains("\"aud\":\"shiny-app\"", one);

        var descriptor = Descriptor();
        descriptor.AddAudience("shiny-web");
        var many = Encoding.UTF8.GetString(Base64Url.Decode(generator.Create(descriptor).Split('.')[1]));
        Assert.Contains("\"aud\":[\"shiny-app\",\"shiny-web\"]", many);
    }

    [Fact]
    public void Rejects_a_registered_claim_smuggled_in_as_a_custom_one()
    {
        using var key = JwtSigningKey.FromSecret(Secret);

        var descriptor = Descriptor();
        descriptor.AddClaim(JwtClaimNames.Issuer, "someone-else");

        Assert.Throws<InvalidOperationException>(() => new JwtTokenGenerator(key).Create(descriptor));
    }
}

public class JwtValidationTests
{
    static readonly byte[] Secret = Encoding.UTF8.GetBytes("a-32-byte-secret-for-testing!!!!");

    static JwtValidationParameters Parameters(JwtSigningKey key) => new()
    {
        ValidIssuers = { "shiny" },
        ValidAudiences = { "shiny-app" },
        SigningKeys = { key }
    };

    static JwtTokenDescriptor Descriptor() => new()
    {
        Issuer = "shiny",
        Subject = "user-1",
        Audiences = { "shiny-app" }
    };

    [Fact]
    public void Rejects_a_tampered_payload()
    {
        using var key = JwtSigningKey.FromSecret(Secret);
        var token = new JwtTokenGenerator(key).Create(Descriptor());

        var parts = token.Split('.');
        var payload = Encoding.UTF8.GetString(Base64Url.Decode(parts[1])).Replace("user-1", "user-2");
        var tampered = $"{parts[0]}.{Base64Url.EncodeString(payload)}.{parts[2]}";

        var result = new JwtTokenValidator(Parameters(key)).Validate(tampered);

        Assert.False(result.IsValid);
        Assert.Contains("signature", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_a_token_signed_with_a_different_secret()
    {
        using var signing = JwtSigningKey.FromSecret(Secret);
        using var other = JwtSigningKey.FromSecret(Encoding.UTF8.GetBytes("a-different-32-byte-secret!!!!!!"));

        var token = new JwtTokenGenerator(other).Create(Descriptor());

        Assert.False(new JwtTokenValidator(Parameters(signing)).Validate(token).IsValid);
    }

    [Fact]
    public void Rejects_an_unsigned_token()
    {
        // The alg:none forgery: strip the signature and claim the token needs none.
        var header = Base64Url.EncodeString("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64Url.EncodeString(
            $$"""{"iss":"shiny","aud":"shiny-app","sub":"attacker","exp":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}"""
        );

        using var key = JwtSigningKey.FromSecret(Secret);
        var result = new JwtTokenValidator(Parameters(key)).Validate($"{header}.{payload}.");

        Assert.False(result.IsValid);
        Assert.Contains("Unsigned", result.Error!);
    }

    [Fact]
    public void Rejects_a_token_whose_header_asks_for_an_algorithm_no_key_implements()
    {
        // Algorithm confusion: an RS256 server handed an HS256 token. The key decides the
        // algorithm here, so the header's request is simply refused.
        using var hmac = JwtSigningKey.FromSecret(Secret);
        var token = new JwtTokenGenerator(hmac).Create(Descriptor());

        using var rsa = RSA.Create(2048);
        using var rsaKey = JwtSigningKey.FromRsa(rsa);

        var result = new JwtTokenValidator(Parameters(rsaKey)).Validate(token);

        Assert.False(result.IsValid);
        Assert.Contains("HS256", result.Error!);
    }

    [Fact]
    public void Rejects_an_expired_token()
    {
        using var key = JwtSigningKey.FromSecret(Secret);

        var descriptor = Descriptor();
        descriptor.IssuedAt = DateTimeOffset.UtcNow.AddHours(-2);
        descriptor.ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1);

        var result = new JwtTokenValidator(Parameters(key)).Validate(new JwtTokenGenerator(key).Create(descriptor));

        Assert.False(result.IsValid);
        Assert.Contains("expired", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Accepts_a_token_that_expired_within_the_clock_skew()
    {
        using var key = JwtSigningKey.FromSecret(Secret);

        var descriptor = Descriptor();
        descriptor.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-30);

        var parameters = Parameters(key);
        parameters.ClockSkew = TimeSpan.FromMinutes(2);

        Assert.True(new JwtTokenValidator(parameters).Validate(new JwtTokenGenerator(key).Create(descriptor)).IsValid);
    }

    [Fact]
    public void Rejects_a_token_that_is_not_valid_yet()
    {
        using var key = JwtSigningKey.FromSecret(Secret);

        var descriptor = Descriptor();
        descriptor.NotBefore = DateTimeOffset.UtcNow.AddHours(1);
        descriptor.ExpiresAt = DateTimeOffset.UtcNow.AddHours(2);

        var result = new JwtTokenValidator(Parameters(key)).Validate(new JwtTokenGenerator(key).Create(descriptor));

        Assert.False(result.IsValid);
        Assert.Contains("not valid yet", result.Error!);
    }

    [Fact]
    public void Rejects_the_wrong_issuer_and_audience()
    {
        using var key = JwtSigningKey.FromSecret(Secret);
        var generator = new JwtTokenGenerator(key);

        var wrongIssuer = Descriptor();
        wrongIssuer.Issuer = "somebody-else";
        Assert.Contains("issuer", new JwtTokenValidator(Parameters(key)).Validate(generator.Create(wrongIssuer)).Error!);

        var wrongAudience = new JwtTokenDescriptor { Issuer = "shiny", Audiences = { "another-app" } };
        Assert.Contains("audience", new JwtTokenValidator(Parameters(key)).Validate(generator.Create(wrongAudience)).Error!);
    }

    [Fact]
    public void Refuses_to_validate_when_issuer_checking_is_on_but_unconfigured()
    {
        using var key = JwtSigningKey.FromSecret(Secret);
        var token = new JwtTokenGenerator(key).Create(Descriptor());

        var parameters = new JwtValidationParameters { SigningKeys = { key }, ValidateAudience = false };

        // Failing closed: an empty allow-list must never mean "allow everything".
        Assert.False(new JwtTokenValidator(parameters).Validate(token).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("only.two")]
    [InlineData("a.b.c.d.e")]
    [InlineData("!!!.!!!.!!!")]
    public void Rejects_malformed_tokens(string token)
    {
        using var key = JwtSigningKey.FromSecret(Secret);
        Assert.False(new JwtTokenValidator(Parameters(key)).Validate(token).IsValid);
    }

    [Fact]
    public void Rejects_a_token_with_no_expiry_by_default()
    {
        using var key = JwtSigningKey.FromSecret(Secret);

        var header = Base64Url.EncodeString("""{"alg":"HS256","typ":"JWT"}""");
        var payload = Base64Url.EncodeString("""{"iss":"shiny","aud":"shiny-app","sub":"forever"}""");
        var signature = Base64Url.Encode(key.Sign(Encoding.ASCII.GetBytes($"{header}.{payload}")));

        var result = new JwtTokenValidator(Parameters(key)).Validate($"{header}.{payload}.{signature}");

        Assert.False(result.IsValid);
        Assert.Contains("does not expire", result.Error!);
    }

    [Fact]
    public void Supports_key_rotation_by_accepting_any_configured_key()
    {
        using var previous = JwtSigningKey.CreateSecret();
        using var current = JwtSigningKey.CreateSecret();

        var token = new JwtTokenGenerator(previous).Create(Descriptor());

        var parameters = new JwtValidationParameters
        {
            ValidIssuers = { "shiny" },
            ValidAudiences = { "shiny-app" },
            SigningKeys = { current, previous }
        };

        Assert.True(new JwtTokenValidator(parameters).Validate(token).IsValid);
    }

    [Fact]
    public void Honours_a_key_id_when_both_sides_declare_one()
    {
        using var oldKey = JwtSigningKey.CreateSecret();
        oldKey.KeyId = "2025";

        using var newKey = JwtSigningKey.CreateSecret();
        newKey.KeyId = "2026";

        var token = new JwtTokenGenerator(newKey).Create(Descriptor());
        Assert.Contains("\"kid\":\"2026\"", Encoding.UTF8.GetString(Base64Url.Decode(token.Split('.')[0])));

        var parameters = new JwtValidationParameters
        {
            ValidIssuers = { "shiny" },
            ValidAudiences = { "shiny-app" },
            SigningKeys = { oldKey, newKey }
        };

        Assert.True(new JwtTokenValidator(parameters).Validate(token).IsValid);
    }

    [Fact]
    public void Rejects_a_secret_too_short_for_its_algorithm()
        => Assert.Throws<ArgumentException>(() => JwtSigningKey.FromSecret("too-short"));

    [Fact]
    public void A_public_only_key_validates_but_cannot_sign()
    {
        using var rsa = RSA.Create(2048);
        using var privateKey = JwtSigningKey.FromRsa(rsa);
        var token = new JwtTokenGenerator(privateKey).Create(Descriptor());

        using var publicOnly = RSA.Create();
        publicOnly.ImportParameters(rsa.ExportParameters(includePrivateParameters: false));
        using var verifyOnly = JwtSigningKey.FromRsa(publicOnly);

        Assert.False(verifyOnly.CanSign);
        Assert.True(new JwtTokenValidator(Parameters(verifyOnly)).Validate(token).IsValid);
        Assert.Throws<InvalidOperationException>(() => new JwtTokenGenerator(verifyOnly).Create(Descriptor()));
    }
}

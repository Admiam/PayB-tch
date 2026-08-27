using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Paybitch.Api.Common.Auth;

/// <summary>
/// Owns the ES256 signing/verification keys (§4.1). Loads the active (and optional previous) key from
/// PEM in config/env; when none is configured it generates an EPHEMERAL P-256 key so the app runs
/// with no secrets (dev only — tokens die on restart). Every issued token carries the active key's
/// <c>kid</c> in its header; verification accepts every loaded key's <c>kid</c>, so a rotation that
/// publishes a new active key and keeps the previous as verify-only bridges one access-token TTL.
/// </summary>
public sealed class Es256KeyProvider : IDisposable
{
    private const string EnvActivePem = "JWT_SIGNING_KEY_PEM";
    private const string EnvPreviousPem = "JWT_PREVIOUS_KEY_PEM";

    private readonly List<ECDsa> _owned = [];

    /// <summary>Signing credentials for newly issued access tokens (active key, ES256).</summary>
    public SigningCredentials SigningCredentials { get; }

    /// <summary>Active key id, present in every issued token's header.</summary>
    public string ActiveKid { get; }

    /// <summary>Every key accepted at verification, keyed by <c>kid</c> (active + optional previous).</summary>
    public IReadOnlyList<SecurityKey> ValidationKeys { get; }

    /// <summary>True when running on an ephemeral dev key (no PEM configured).</summary>
    public bool IsEphemeral { get; }

    public Es256KeyProvider(IOptions<JwtOptions> options, ILogger<Es256KeyProvider> logger)
    {
        var opts = options.Value;
        var keys = new List<SecurityKey>();

        var activePem = FirstNonBlank(opts.SigningKeyPem, Environment.GetEnvironmentVariable(EnvActivePem));
        SecurityKey activeKey;
        if (activePem is not null)
        {
            activeKey = LoadKey(activePem, opts.SigningKid);
            IsEphemeral = false;
        }
        else
        {
            activeKey = GenerateEphemeralKey(opts.SigningKid);
            IsEphemeral = true;
            logger.LogWarning(
                "No {Env} configured — using an EPHEMERAL ES256 signing key (kid={Kid}). Access tokens will not survive a restart; configure a PEM secret for any shared deployment.",
                EnvActivePem, activeKey.KeyId);
        }

        ActiveKid = activeKey.KeyId!;
        keys.Add(activeKey);
        SigningCredentials = new SigningCredentials(activeKey, SecurityAlgorithms.EcdsaSha256);

        var previousPem = FirstNonBlank(opts.PreviousKeyPem, Environment.GetEnvironmentVariable(EnvPreviousPem));
        if (previousPem is not null)
            keys.Add(LoadKey(previousPem, opts.PreviousKid));

        ValidationKeys = keys;
    }

    /// <summary>Build the token-validation parameters JwtBearer uses (ES256, iss/aud/lifetime, all kids).</summary>
    public TokenValidationParameters CreateValidationParameters(JwtOptions options) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = options.Issuer,
        ValidateAudience = true,
        ValidAudience = options.Audience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKeys = ValidationKeys,
        ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = AuthClaims.Subject,
    };

    private ECDsaSecurityKey LoadKey(string pem, string? explicitKid)
    {
        var ecdsa = ECDsa.Create();
        _owned.Add(ecdsa);
        ecdsa.ImportFromPem(pem);
        return new ECDsaSecurityKey(ecdsa) { KeyId = explicitKid ?? DeriveKid(ecdsa) };
    }

    private ECDsaSecurityKey GenerateEphemeralKey(string? explicitKid)
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _owned.Add(ecdsa);
        return new ECDsaSecurityKey(ecdsa) { KeyId = explicitKid ?? "dev-" + DeriveKid(ecdsa) };
    }

    // Stable short thumbprint over the public SubjectPublicKeyInfo.
    private static string DeriveKid(ECDsa ecdsa)
    {
        var spki = ecdsa.ExportSubjectPublicKeyInfo();
        var hash = SHA256.HashData(spki);
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static string? FirstNonBlank(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

    public void Dispose()
    {
        foreach (var ecdsa in _owned)
            ecdsa.Dispose();
    }
}

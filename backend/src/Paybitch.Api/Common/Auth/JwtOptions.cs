namespace Paybitch.Api.Common.Auth;

/// <summary>
/// Access-token JWT configuration (§4.1), bound from the <c>Jwt</c> section. The signing material is
/// ES256 PEM supplied out-of-band via secret env (<c>JWT_SIGNING_KEY_PEM</c> active,
/// <c>JWT_PREVIOUS_KEY_PEM</c> verify-only) — never in source. When no PEM is configured the key
/// provider falls back to an ephemeral dev key so the app runs without secrets.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>Token issuer (<c>iss</c>).</summary>
    public string Issuer { get; set; } = "https://api.paybitch.app";

    /// <summary>Token audience (<c>aud</c>).</summary>
    public string Audience { get; set; } = "paybitch-ios";

    /// <summary>Active signing key PEM (ES256 private key). Falls back to env <c>JWT_SIGNING_KEY_PEM</c>.</summary>
    public string? SigningKeyPem { get; set; }

    /// <summary>Optional explicit <c>kid</c> for the active key; derived from the key when absent.</summary>
    public string? SigningKid { get; set; }

    /// <summary>Optional previous key PEM (verify-only, one TTL of rotation grace). Falls back to env <c>JWT_PREVIOUS_KEY_PEM</c>.</summary>
    public string? PreviousKeyPem { get; set; }

    /// <summary>Optional explicit <c>kid</c> for the previous key; derived from the key when absent.</summary>
    public string? PreviousKid { get; set; }
}

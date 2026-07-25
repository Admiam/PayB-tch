using Microsoft.Extensions.Configuration;

namespace Paybitch.Api.Features.Auth.Apple;

/// <summary>
/// Sign in with Apple configuration (§4.1), read from the <c>Apple</c> configuration section. All
/// values are supplied out-of-band (secret env / config) — never in source (§6). When the core
/// verification inputs (<see cref="BundleId"/>) are absent the server cannot validate an Apple
/// identity token and <c>POST /auth/apple</c> returns a <c>501</c> problem (dev-without-Apple gate),
/// while the full validation code path stays intact.
/// </summary>
public sealed class AppleAuthOptions
{
    public const string SectionName = "Apple";

    /// <summary>The app's bundle id — the <c>aud</c> claim every Apple identity token must carry.</summary>
    public string? BundleId { get; init; }

    /// <summary>Apple Developer Team id — <c>iss</c> of the ES256 client-secret JWT (code exchange).</summary>
    public string? TeamId { get; init; }

    /// <summary>Key id of the Sign in with Apple <c>.p8</c> signing key (JWT <c>kid</c> header).</summary>
    public string? KeyId { get; init; }

    /// <summary>The Sign in with Apple <c>.p8</c> private key, PEM-encoded (ES256). Secret.</summary>
    public string? PrivateKeyPem { get; init; }

    /// <summary>
    /// Base64 of a 32-byte AES-256-GCM key used to encrypt the Apple refresh token at rest
    /// (<c>auth_identities.apple_refresh_token_enc</c>). Absent ⇒ the token is never persisted in
    /// plaintext (redemption still runs; the encrypted copy is simply skipped, logged once).
    /// </summary>
    public string? TokenEncryptionKeyBase64 { get; init; }

    /// <summary>True when an identity token can be verified (bundle id present).</summary>
    public bool CanValidateIdentity => !string.IsNullOrWhiteSpace(BundleId);

    /// <summary>True when the authorization-code exchange + Apple revoke can run (all signing inputs present).</summary>
    public bool CanExchangeCode =>
        !string.IsNullOrWhiteSpace(TeamId)
        && !string.IsNullOrWhiteSpace(KeyId)
        && !string.IsNullOrWhiteSpace(PrivateKeyPem)
        && !string.IsNullOrWhiteSpace(BundleId);

    /// <summary>Bind the options from the <c>Apple</c> configuration section (no DI registration needed).</summary>
    public static AppleAuthOptions From(IConfiguration configuration)
        => configuration.GetSection(SectionName).Get<AppleAuthOptions>() ?? new AppleAuthOptions();
}

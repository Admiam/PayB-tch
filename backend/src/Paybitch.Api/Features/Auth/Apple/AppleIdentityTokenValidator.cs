using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Paybitch.Api.Features.Auth.Apple;

/// <summary>The outcome of validating an Apple identity token (§4.1 step 2).</summary>
public sealed record AppleIdentityResult(
    bool IsValid,
    string? Subject = null,
    string? Email = null,
    bool EmailVerified = false)
{
    public static readonly AppleIdentityResult Invalid = new(false);
}

/// <summary>
/// Validates an Apple <c>identityToken</c> against Apple's JWKS (§4.1): signature, <c>iss</c>
/// (<c>https://appleid.apple.com</c>), <c>aud</c> (the configured bundle id), <c>exp</c>, and the
/// <c>nonce</c> — the token's <c>nonce</c> claim must equal <c>hex(SHA-256(rawNonce))</c>, and BOTH
/// sides must be present (absent on either ⇒ invalid, 🔧 absent ≠ pass). The JWKS
/// <see cref="ConfigurationManager{T}"/> is a process-wide singleton so Apple's keys are fetched once
/// and auto-refreshed; it owns its own <see cref="HttpClient"/>, so no DI registration is required.
/// </summary>
public static class AppleIdentityTokenValidator
{
    private const string AppleIssuer = "https://appleid.apple.com";
    private const string AppleMetadataUrl = "https://appleid.apple.com/.well-known/openid-configuration";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly JsonWebTokenHandler Handler = new();

    private static readonly ConfigurationManager<OpenIdConnectConfiguration> ConfigManager = new(
        AppleMetadataUrl,
        new OpenIdConnectConfigurationRetriever(),
        new HttpDocumentRetriever(Http) { RequireHttps = true });

    /// <summary>Validate the token; a failure of ANY check collapses to <see cref="AppleIdentityResult.Invalid"/>.</summary>
    public static async Task<AppleIdentityResult> ValidateAsync(
        string identityToken,
        string rawNonce,
        string audience,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(identityToken) || string.IsNullOrWhiteSpace(rawNonce))
            return AppleIdentityResult.Invalid;

        OpenIdConnectConfiguration config;
        try
        {
            config = await ConfigManager.GetConfigurationAsync(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // JWKS fetch failed — treat as an invalid-token verdict (401), never a 500 leak.
            return AppleIdentityResult.Invalid;
        }

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = AppleIssuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = config.SigningKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            NameClaimType = "sub",
        };

        var result = await Handler.ValidateTokenAsync(identityToken, parameters);
        if (!result.IsValid)
            return AppleIdentityResult.Invalid;

        // Nonce: the claim MUST be present, and MUST equal hex(SHA-256(rawNonce)) (§4.1).
        if (!result.Claims.TryGetValue("nonce", out var nonceClaim)
            || nonceClaim is not string tokenNonce || string.IsNullOrEmpty(tokenNonce))
            return AppleIdentityResult.Invalid;

        var expectedNonce = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawNonce))).ToLowerInvariant();
        if (!FixedTimeEquals(expectedNonce, tokenNonce))
            return AppleIdentityResult.Invalid;

        if (!result.Claims.TryGetValue("sub", out var subClaim)
            || subClaim is not string subject || string.IsNullOrEmpty(subject))
            return AppleIdentityResult.Invalid;

        var email = result.Claims.TryGetValue("email", out var emailClaim) ? emailClaim as string : null;
        var emailVerified = result.Claims.TryGetValue("email_verified", out var verifiedClaim)
            && ParseEmailVerified(verifiedClaim);

        return new AppleIdentityResult(true, subject, email, emailVerified);
    }

    // Apple sends email_verified as either a bool OR the string "true"/"false" — parse both (§4.1).
    private static bool ParseEmailVerified(object? claim) => claim switch
    {
        bool b => b,
        string s => bool.TryParse(s, out var v) && v,
        _ => false,
    };

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a),
            Encoding.UTF8.GetBytes(b.ToLowerInvariant()));
}

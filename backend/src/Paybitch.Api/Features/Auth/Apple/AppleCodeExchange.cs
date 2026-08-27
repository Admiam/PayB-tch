using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Paybitch.Infrastructure.Abstractions;

namespace Paybitch.Api.Features.Auth.Apple;

/// <summary>
/// Server-side redemption of the Sign in with Apple <c>authorizationCode</c> and the account-deletion
/// revoke (§4.1 / §4.4 step 9). Both call <c>https://appleid.apple.com/auth/token|revoke</c>
/// authenticated with an ES256 <b>client-secret JWT</b> signed by the <c>.p8</c> key. All network I/O
/// is best-effort and fully guarded: a failed redemption is NON-FATAL to login (the identity token,
/// already JWKS-validated, is the authentication proof), and the class only runs when
/// <see cref="AppleAuthOptions.CanExchangeCode"/> is true.
/// </summary>
public sealed class AppleCodeExchange(AppleAuthOptions options, IClock clock)
{
    private const string TokenEndpoint = "https://appleid.apple.com/auth/token";
    private const string RevokeEndpoint = "https://appleid.apple.com/auth/revoke";
    private const string Audience = "https://appleid.apple.com";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly JsonWebTokenHandler Handler = new();

    /// <summary>Redeem an authorization code → Apple refresh token. Returns null on any failure (non-fatal).</summary>
    public async Task<string?> RedeemAsync(string authorizationCode, CancellationToken ct)
    {
        if (!options.CanExchangeCode || string.IsNullOrWhiteSpace(authorizationCode))
            return null;

        var form = new Dictionary<string, string>
        {
            ["client_id"] = options.BundleId!,
            ["client_secret"] = CreateClientSecret(),
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode,
        };

        using var response = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
    }

    /// <summary>Revoke an Apple refresh token (App Review 5.1.1(v)). Returns true on 2xx, false otherwise.</summary>
    public async Task<bool> RevokeAsync(string appleRefreshToken, CancellationToken ct)
    {
        if (!options.CanExchangeCode || string.IsNullOrWhiteSpace(appleRefreshToken))
            return false;

        var form = new Dictionary<string, string>
        {
            ["client_id"] = options.BundleId!,
            ["client_secret"] = CreateClientSecret(),
            ["token"] = appleRefreshToken,
            ["token_type_hint"] = "refresh_token",
        };

        using var response = await Http.PostAsync(RevokeEndpoint, new FormUrlEncodedContent(form), ct);
        return response.IsSuccessStatusCode;
    }

    // The ES256 client-secret JWT (iss=teamId, sub=bundleId, aud=appleid.apple.com, kid=keyId).
    private string CreateClientSecret()
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(options.PrivateKeyPem!);
        var key = new ECDsaSecurityKey(ecdsa) { KeyId = options.KeyId };

        var now = clock.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.TeamId,
            Audience = Audience,
            IssuedAt = now.UtcDateTime,
            Expires = now.AddMinutes(5).UtcDateTime,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256),
            Claims = new Dictionary<string, object> { ["sub"] = options.BundleId! },
        };

        return Handler.CreateToken(descriptor);
    }
}

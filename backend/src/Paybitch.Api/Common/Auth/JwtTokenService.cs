using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Paybitch.Api.Common.Options;
using Paybitch.Infrastructure.Abstractions;

namespace Paybitch.Api.Common.Auth;

/// <summary>An issued access token plus the metadata the <c>/auth</c> response echoes.</summary>
public sealed record AccessToken(string Value, int ExpiresInSeconds, DateTimeOffset ExpiresAt, string Jti);

/// <summary>
/// Issues (and describes how to validate) short-lived ES256 access JWTs (§4.1). Carries <c>sub</c>,
/// <c>epoch</c>, <c>jti</c> and standard time claims; NO group claims — membership is resolved live
/// per request (D6). The <c>epoch</c> value MUST be a live DB read of <c>users.token_epoch</c> at the
/// call site, never a cached one (§4.1).
/// </summary>
public sealed class JwtTokenService(
    Es256KeyProvider keys,
    IOptions<JwtOptions> jwtOptions,
    IOptions<OperationalConstants> ops,
    IClock clock)
{
    private static readonly JsonWebTokenHandler Handler = new();

    /// <summary>Mint an access token for a user at the given (live-read) epoch.</summary>
    public AccessToken IssueAccessToken(Guid userId, long epoch)
    {
        var now = clock.UtcNow;
        var expires = now + ops.Value.AccessTokenTtl;
        var jti = Guid.NewGuid().ToString("N");

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwtOptions.Value.Issuer,
            Audience = jwtOptions.Value.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = keys.SigningCredentials,
            Claims = new Dictionary<string, object>
            {
                [AuthClaims.Subject] = userId.ToString(),
                [AuthClaims.Epoch] = epoch,
                [AuthClaims.JwtId] = jti,
            },
        };

        var token = Handler.CreateToken(descriptor);
        return new AccessToken(token, (int)ops.Value.AccessTokenTtl.TotalSeconds, expires, jti);
    }

    /// <summary>The token-validation parameters (also used to configure JwtBearer).</summary>
    public TokenValidationParameters ValidationParameters => keys.CreateValidationParameters(jwtOptions.Value);
}

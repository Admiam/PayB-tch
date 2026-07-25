namespace Paybitch.Api.Common.Auth;

/// <summary>Access-token claim names (§4.1). No group claims — membership is checked live per request (D6).</summary>
public static class AuthClaims
{
    /// <summary>Subject — the <c>users.id</c> (GUID).</summary>
    public const string Subject = "sub";

    /// <summary>Monotonic <c>users.token_epoch</c> stamped at issuance; enforced per request (§4.1).</summary>
    public const string Epoch = "epoch";

    /// <summary>Unique token id (replay/theft tracing).</summary>
    public const string JwtId = "jti";
}

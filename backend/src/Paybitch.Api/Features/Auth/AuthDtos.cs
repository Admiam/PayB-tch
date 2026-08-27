namespace Paybitch.Api.Features.Auth;

/// <summary>
/// <c>POST /auth/apple</c> body (§4.1). <see cref="FullName"/> is present only on Apple's first-consent
/// callback and is honoured ONLY when <c>(provider, subject)</c> is new — a later login can never
/// rename an existing account.
/// </summary>
public sealed record AppleSignInRequest(
    string? IdentityToken,
    string? AuthorizationCode,
    string? Nonce,
    AppleFullName? FullName);

/// <summary>Apple's client-side name components (never inside the JWT).</summary>
public sealed record AppleFullName(string? GivenName, string? FamilyName);

/// <summary><c>POST /auth/refresh</c> body — the opaque refresh token to rotate.</summary>
public sealed record RefreshRequest(string? RefreshToken);

/// <summary>
/// <c>POST /auth/logout</c> body. Revokes the presented refresh token; <see cref="AllSessions"/> also
/// bumps <c>token_epoch</c> (kills outstanding access tokens) and deletes ALL the caller's devices;
/// <see cref="DeviceId"/> deletes just that device row alongside the revoke (§4.5).
/// </summary>
public sealed record LogoutRequest(string? RefreshToken, Guid? DeviceId, bool AllSessions = false);

/// <summary>The <c>/auth/apple</c> success body: the token pair, its TTL, and the resolved user (§4.1).</summary>
public sealed record AuthSessionResponse(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    AuthUser User);

/// <summary>The token pair returned by <c>/auth/refresh</c> (no user echo — the client already has it).</summary>
public sealed record TokenPairResponse(string AccessToken, string RefreshToken, int ExpiresIn);

/// <summary>The user projection embedded in the auth response (§4.1 wire contract).</summary>
public sealed record AuthUser(
    string Id,
    string DisplayName,
    string? Email,
    string DefaultCurrency,
    string Locale,
    bool IsNewUser);

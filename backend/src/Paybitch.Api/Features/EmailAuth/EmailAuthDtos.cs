namespace Paybitch.Api.Features.EmailAuth;

/// <summary>
/// E6 wire DTOs (§6.4). Every request is an explicit shape (§4.3 over-post guard): the caller can bind
/// only the fields below — <c>userId</c>, <c>purpose</c>, <c>email_verified_at</c>, <c>consumed_at</c>
/// are server-owned and never wire-bound. Session-issuing responses reuse the v1 §4.1 token shapes
/// (<c>Paybitch.Api.Features.Auth.AuthSessionResponse</c> / <c>AuthUser</c>).
/// </summary>
/// <remarks>
/// The verify DTOs deliberately differ: the unauthenticated <c>/auth/email/verify</c> binds
/// <c>{email, code}</c> (no session to scope the lookup), while the authenticated link/change verifies
/// bind <c>{code}</c> only — the session is the subject and the code itself disambiguates the pending row.
/// </remarks>
// --- /auth/email/* (unauthenticated) ---
public sealed record EmailStartRequest(string? Email);

public sealed record EmailVerifyRequest(string? Email, string? Code);

// --- /me/identities/link/* (authenticated: side-A session + side-B OTP, EXT-D6d) ---
public sealed record LinkStartRequest(string? Email);

public sealed record LinkVerifyRequest(string? Code);

// --- /me/email/change/* (authenticated: OTP to the NEW address, EXT-D6h) ---
public sealed record EmailChangeStartRequest(string? Email);

public sealed record EmailChangeVerifyRequest(string? Code);

/// <summary>The uniform, enumeration-safe body every <c>…/start</c> returns (EXT-D6f).</summary>
public sealed record SentResponse(string Status)
{
    public static readonly SentResponse Sent = new("sent");
}

/// <summary>One linked identity as surfaced by <c>GET /me/identities</c> — provider + a MASKED subject (§6.4).</summary>
public sealed record IdentitySummary(string Provider, string MaskedSubject, DateTimeOffset LinkedAt);

/// <summary>The <c>GET /me/identities</c> body: the caller's linked providers, so the client knows if unlink is safe.</summary>
public sealed record IdentitiesResponse(IReadOnlyList<IdentitySummary> Identities);

/// <summary>The <c>/me/email/change/verify</c> success body — the new contact email is set and verified.</summary>
public sealed record EmailChangeResponse(string Email, bool EmailVerified);

namespace Paybitch.Api.Features.EmailAuth;

/// <summary>
/// E6-specific problem <c>code</c>s (§6.4). These are <b>additive</b> to the v1 Appendix B catalog
/// (<c>Paybitch.Api.Common.Errors.ProblemCodes</c>) — a new endpoint returning a new code is not a
/// breaking change (§3.11). They are declared here (not in the Common catalog, which this slice must not
/// edit); the orchestrator SHOULD fold them into <c>ProblemCodes</c>/<c>Problems.Titles</c> during
/// convergence so the closed-set reflection in <c>ProblemCodes.All</c> recognises them.
/// </summary>
internal static class EmailAuthProblemCodes
{
    /// <summary>401 — the ONE uniform verify failure (wrong ≡ expired ≡ consumed ≡ attempts-exhausted, EXT-D6f).</summary>
    public const string InvalidOrExpiredCode = "invalid_or_expired_code";

    /// <summary>422 — an Apple private-relay / Hide-My-Email address may never back an email credential (EXT-D6c).</summary>
    public const string RelayEmailNotAllowed = "relay_email_not_allowed";

    /// <summary>409 — the target address already backs ANOTHER user's identity; we never auto-merge (EXT-D6c/d).</summary>
    public const string IdentityTaken = "identity_taken";

    /// <summary>409 — refusing to remove the last usable identity (self-lockout); close the account instead (EXT-D6i).</summary>
    public const string LastIdentity = "last_identity";
}

using System.Text;

namespace Paybitch.Api.Features.EmailAuth;

/// <summary>
/// E6 email-auth tunables (§6.2/§6.3), bound from the <c>EmailAuth</c> configuration section the same way
/// <see cref="Paybitch.Api.Features.Auth.Apple.AppleAuthOptions"/> reads <c>Apple</c> — no Program.cs
/// registration (this slice owns no shared file). Defaults mirror Appendix A / EXT-D6e so the app runs
/// with zero config in dev; a deployment overrides any value via config.
/// </summary>
public sealed class EmailAuthConfig
{
    public const string SectionName = "EmailAuth";

    /// <summary>
    /// App-level pepper mixed into <c>SHA-256(pepper ‖ code)</c> so a DB-only leak is not offline-reversible
    /// (EXT-D6e). MUST be set from a secret env in production; the dev fallback below is NOT a secret.
    /// </summary>
    public string? Pepper { get; init; }

    /// <summary>Universal-link base the login email embeds (<c>?e=&amp;c=</c>), §6.4.</summary>
    public string UniversalLinkBase { get; init; } = "https://paybitch.app/auth/email";

    /// <summary>Per-code verify attempt ceiling; the row invalidates at/after this (EXT-D6e: ≤5).</summary>
    public int MaxVerifyAttempts { get; init; } = 5;

    /// <summary>OTP time-to-live (EXT-D6e / Appendix A: 10 min).</summary>
    public int OtpTtlMinutes { get; init; } = 10;

    /// <summary>Prune grace past expiry before a token row is DELETEd (§6.3).</summary>
    public int PruneGraceMinutes { get; init; } = 5;

    public TimeSpan OtpTtl => TimeSpan.FromMinutes(OtpTtlMinutes);
    public TimeSpan PruneGrace => TimeSpan.FromMinutes(PruneGraceMinutes);

    /// <summary>True when a real production pepper is configured (a warning is logged otherwise).</summary>
    public bool HasConfiguredPepper => !string.IsNullOrWhiteSpace(Pepper);

    /// <summary>Bind from the <c>EmailAuth</c> section; falls back entirely to defaults when absent.</summary>
    public static EmailAuthConfig From(IConfiguration configuration)
        => configuration.GetSection(SectionName).Get<EmailAuthConfig>() ?? new EmailAuthConfig();

    // Deterministic, non-secret fallback so OTP hashing works in dev. Offline-leak protection is void
    // under this value — production MUST set EmailAuth:Pepper.
    private const string DevPepper = "paybitch-dev-otp-pepper-not-for-production";

    /// <summary>The pepper bytes prefixed to a code before hashing.</summary>
    public byte[] PepperBytes =>
        Encoding.UTF8.GetBytes(HasConfiguredPepper ? Pepper! : DevPepper);
}

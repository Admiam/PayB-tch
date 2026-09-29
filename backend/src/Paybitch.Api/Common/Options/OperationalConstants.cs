namespace Paybitch.Api.Common.Options;

/// <summary>
/// The single home for every v1 operational tunable (BACKEND_DESIGN Appendix A). Bound from the
/// <c>Operational</c> configuration section as <see cref="Microsoft.Extensions.Options.IOptions{T}"/>.
/// Defaults here mirror the authoritative Appendix A values so the app runs with zero config; a
/// deployment overrides any value via config, never a code edit.
/// </summary>
public sealed class OperationalConstants
{
    public const string SectionName = "Operational";

    // --- Tokens (§4.1) ---
    /// <summary>Access token TTL — Appendix A: 15 min.</summary>
    public TimeSpan AccessTokenTtl { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Refresh token sliding lifetime, re-armed on each rotation — Appendix A: 60 days.</summary>
    public TimeSpan RefreshTokenSlidingLifetime { get; set; } = TimeSpan.FromDays(60);

    /// <summary>Refresh token absolute lifetime; family dies regardless of activity — Appendix A: 180 days.</summary>
    public TimeSpan RefreshTokenAbsoluteLifetime { get; set; } = TimeSpan.FromDays(180);

    /// <summary>Grace window in which a just-rotated refresh token replays its successor — Appendix A: 30 s.</summary>
    public TimeSpan RefreshRotationGraceWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound of the per-instance in-memory <c>token_epoch</c> cache — Appendix A: 60 s.</summary>
    public TimeSpan TokenEpochCacheTtl { get; set; } = TimeSpan.FromSeconds(60);

    // --- Invites (§3.8) ---
    /// <summary>Invite TTL; after this an invite row is <c>410 invite_expired</c> — Appendix A: 7 days.</summary>
    public TimeSpan InviteTtl { get; set; } = TimeSpan.FromDays(7);

    // --- Rate limits (§4.3) ---
    /// <summary>Requests per minute per IP on <c>/auth/*</c> — Appendix A: 10.</summary>
    public int RateLimitAuthPerIpPerMinute { get; set; } = 10;

    /// <summary>First-provisioning exchanges per Apple <c>sub</c> per day — Appendix A: 5.</summary>
    public int RateLimitProvisioningPerIdentityPerDay { get; set; } = 5;

    /// <summary>Invite creations per user per day — Appendix A: 20.</summary>
    public int RateLimitInviteCreatePerUserPerDay { get; set; } = 20;

    /// <summary>Unauthenticated invite previews per minute per IP — Appendix A: 30.</summary>
    public int RateLimitInvitePreviewPerIpPerMinute { get; set; } = 30;

    // --- Retention (§4.3, §3.5) ---
    /// <summary>activity_log retention / GDPR window — Appendix A: 24 months (~730 days).</summary>
    public TimeSpan ActivityLogRetention { get; set; } = TimeSpan.FromDays(730);

    /// <summary>change_log retention = sync-cursor lifetime; older cursor ⇒ <c>410 sync_cursor_expired</c> — Appendix A: 90 days.</summary>
    public TimeSpan ChangeLogRetention { get; set; } = TimeSpan.FromDays(90);

    // --- Sync (§3.5.4) ---
    /// <summary><c>/sync</c> default page size — Appendix A: 100.</summary>
    public int SyncPageSizeDefault { get; set; } = 100;

    /// <summary><c>/sync</c> maximum page size (clamped) — Appendix A: 500.</summary>
    public int SyncPageSizeMax { get; set; } = 500;

    /// <summary>The <c>pg_advisory_xact_lock</c> key serializing change_log INSERT visibility (§3.5.3) — Appendix A: 0x5041594249545348 ("PAYBITSH").</summary>
    public long ChangeLogLockKey { get; set; } = 0x5041594249545348L;

    // --- Push (§4.5) ---
    /// <summary>Silent-push fan-out debounce inside the worker — Appendix A: 30 s per device.</summary>
    public TimeSpan PushDebounce { get; set; } = TimeSpan.FromSeconds(30);

    // --- Caps (Appendix A) ---
    /// <summary>Serialized cap on activity_log.metadata, app-enforced at write time — Appendix A: 4 KB.</summary>
    public int ActivityLogMetadataCapBytes { get; set; } = 4096;

    /// <summary>Registered devices per user; an 11th evicts the least-recently-updated — Appendix A: 10.</summary>
    public int DevicesPerUser { get; set; } = 10;

    /// <summary>Members per group; checked on any op creating a group_members row — Appendix A: 50.</summary>
    public int MembersPerGroup { get; set; } = 50;

    /// <summary>Groups a user may belong to — Appendix A: 200.</summary>
    public int GroupsPerUser { get; set; } = 200;

    /// <summary>
    /// Cross-group identity pairings one user may store (E11). Generous against any real ledger —
    /// 200 groups × 50 members is the hard ceiling on rows a caller could even name — and present so
    /// the payload stays bounded rather than to ration a feature.
    /// </summary>
    public int SharedLedgerLinksPerUser { get; set; } = 1000;

    /// <summary>Max notes length on expenses/settlements ⇒ <c>422 notes_too_long</c> — Appendix A: 2000 chars.</summary>
    public int NotesMaxLength { get; set; } = 2000;

    /// <summary>Kestrel MaxRequestBodySize ⇒ <c>413 payload_too_large</c> — Appendix A: 256 KB.</summary>
    public long RequestBodyMaxBytes { get; set; } = 256 * 1024;
}

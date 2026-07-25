namespace Paybitch.Api.Features.Recurring;

/// <summary>The three curated <c>recurring_rules.freq</c> values (EXT-D3a) — never RFC 5545 RRULE.</summary>
public static class Freq
{
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";
    public const string Yearly = "yearly";

    public static bool IsValid(string? f) => f is Weekly or Monthly or Yearly;
}

/// <summary>The <c>recurring_rules.pause_reason</c> value set (EXT-D3e/g/i). NULL when active/running.</summary>
public static class PauseReasons
{
    public const string User = "user";                    // manual pause endpoint
    public const string MemberRemoved = "member_removed"; // a pinned split member was soft-deleted
    public const string PayerRemoved = "payer_removed";   // paid_by was soft-deleted (both split modes)
    public const string BacklogExceeded = "backlog_exceeded"; // due occurrences exceeded the catch-up cap
    public const string GroupArchived = "group_archived"; // firing into an archived group
}

/// <summary>The <c>recurring_rules.status</c> state machine (EXT-D3i): active ⇄ paused, both → ended.</summary>
public static class RuleStatus
{
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Ended = "ended";
}

/// <summary>
/// E3 problem <c>code</c>s (§3.7, additive to Appendix B). These are NOT yet in the shared
/// <see cref="Paybitch.Api.Common.Errors.ProblemCodes"/> catalog, so handlers pass them as string
/// literals through <c>Problems.Validation(code,…)</c> / <c>Problems.Conflict(code)</c> — that sets the
/// wire <c>code</c> directly, independent of the validation-filter twin table. The orchestrator should
/// add them to <c>ProblemCodes</c> for full catalog consistency.
/// </summary>
public static class RecurringProblemCodes
{
    public const string InvalidRecurrence = "invalid_recurrence";           // 422 — shape wrong for freq
    public const string InvalidTimezone = "invalid_timezone";               // 422 — not an IANA tzdb id
    public const string RecurrenceEndAmbiguous = "recurrence_end_ambiguous"; // 422 — endsOn AND count supplied
    public const string RecurringSplitRequired = "recurring_split_required"; // 422 — exact/shares/percent w/o rows
    public const string RuleNotResumable = "rule_not_resumable";            // 409 — pause/resume on an ended rule
    public const string ReservedClientId = "reserved_client_id";            // 422 — user clientId matching ^rec: (expense boundary)
}

/// <summary>Operational caps &amp; tunables for E3 (§3.7, Appendix-A style — one location).</summary>
public static class RecurringLimits
{
    /// <summary>Active rules per group; a create beyond this → 409 <c>limit_exceeded</c>.</summary>
    public const int MaxActiveRulesPerGroup = 50;

    /// <summary>Missed occurrences materialized per rule per worker pass; beyond → auto-pause <c>backlog_exceeded</c>.</summary>
    public const int CatchUpCap = 60;

    /// <summary>Occurrence-preview horizon: rows.</summary>
    public const int PreviewMaxRows = 100;

    /// <summary>Occurrence-preview horizon: months from today (<c>?until=</c> is clamped to this).</summary>
    public const int PreviewHorizonMonths = 24;

    public const int IntervalMin = 1;
    public const int IntervalMax = 60;

    /// <summary>Fire jitter is 0..15 minutes, deterministic on <c>hash(rule_id)</c> (EXT-D3c).</summary>
    public const int JitterMinutesModulo = 16;

    /// <summary>Reserved server-owned expense clientId prefix for fired occurrences (EXT-D3f).</summary>
    public const string FiredClientIdPrefix = "rec:";
}

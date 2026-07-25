using Paybitch.Api.Features.Activity;

namespace Paybitch.Api.Features.Notifications;

/// <summary>
/// The notification channels (EXT-D5b). One override / resolved row per (scope, event, channel).
/// The set mirrors the <c>notification_prefs.channel</c> DB CHECK.
/// </summary>
public static class NotificationChannels
{
    public const string Push = "push";
    public const string Email = "email";

    /// <summary>The closed channel set — a PUT override with anything else is <c>422 invalid_channel</c>.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Push, Email };

    public static bool IsValid(string channel) => All.Contains(channel);
}

/// <summary>
/// The notification event taxonomy IS the §3.10 activity verb taxonomy (EXT-D5c) — one list, not two.
/// Any §3.10 verb is a valid <c>event_type</c>; the extension-introduced verbs (E3
/// <c>recurring.materialized</c>, E4 <c>export.ready</c>) are unioned in additively (§3.11) because
/// they are notifiable defaults but are not part of the frozen v1 <see cref="ActivityVerbs.All"/> set.
/// </summary>
public static class NotificationEvents
{
    /// <summary>E3 additive verb (§3.11) — not in the v1 <see cref="ActivityVerbs"/> catalog.</summary>
    public const string RecurringMaterialized = "recurring.materialized";

    /// <summary>E4 additive verb (§3.11) — not in the v1 <see cref="ActivityVerbs"/> catalog.</summary>
    public const string ExportReady = "export.ready";

    /// <summary>
    /// Every accepted <c>event_type</c>: the v1 verb catalog plus the additive extension verbs. A PUT
    /// override naming anything else is <c>422 unknown_event_type</c> (EXT-D5c / §5.7 over-post guard).
    /// </summary>
    public static readonly IReadOnlySet<string> All = BuildAll();

    public static bool IsValid(string eventType) => All.Contains(eventType);

    private static IReadOnlySet<string> BuildAll()
    {
        var set = new HashSet<string>(ActivityVerbs.All, StringComparer.Ordinal)
        {
            RecurringMaterialized,
            ExportReady,
        };
        return set;
    }
}

/// <summary>
/// Additive Appendix B problem codes owned by E5 (§5.7 / §5.4). These are string literals so they can
/// live in this slice without editing the frozen <c>Common/Errors/ProblemCodes</c> catalog; the
/// orchestrator should register them in Appendix B / <c>ProblemCodes.All</c> at convergence (additive).
/// </summary>
public static class NotificationProblemCodes
{
    public const string InvalidChannel = "invalid_channel";        // 422
    public const string UnknownEventType = "unknown_event_type";   // 422
}

/// <summary>Canonical <c>jobs.kind</c> discriminators owned by E5 (mirrors <c>JobKinds</c>, E0).</summary>
public static class NotificationJobKinds
{
    /// <summary>The fan-out drain: reads activity_log via the notification_cursor watermark and dispatches (EXT-D5e).</summary>
    public const string Fanout = "notification.fanout";

    /// <summary>A single queued transactional/notification email, delivered via <c>IEmailSender</c>.</summary>
    public const string EmailSend = "email.send";
}

/// <summary>The logical <c>notification_cursor.worker</c> id for the fan-out watermark.</summary>
public static class NotificationWorkers
{
    public const string Fanout = "notification.fanout";
}

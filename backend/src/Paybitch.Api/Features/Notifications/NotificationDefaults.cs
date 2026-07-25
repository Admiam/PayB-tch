using Paybitch.Api.Features.Activity;

namespace Paybitch.Api.Features.Notifications;

/// <summary>
/// The hard-coded default-on matrix (EXT-D5b/c, §5.4). <c>event_type</c> accepts any §3.10 verb, but
/// only these (event, channel) pairs default ON; every other pair defaults OFF (user-enable-able via an
/// override row). This is the bottom of the per-group → global → default resolution stack — it is
/// derivable, never materialized (X2).
/// </summary>
public static class NotificationDefaults
{
    // The default-on (event, channel) pairs. Everything not listed defaults off.
    private static readonly HashSet<(string Event, string Channel)> DefaultOn = new()
    {
        (ActivityVerbs.ExpenseCreated, NotificationChannels.Push),
        (ActivityVerbs.ExpenseUpdated, NotificationChannels.Push),
        (ActivityVerbs.SettlementRecorded, NotificationChannels.Push),
        (ActivityVerbs.MemberAdded, NotificationChannels.Push),
        (ActivityVerbs.MemberClaimed, NotificationChannels.Push),
        (ActivityVerbs.InviteAccepted, NotificationChannels.Push),
        (NotificationEvents.RecurringMaterialized, NotificationChannels.Push),
        (NotificationEvents.ExportReady, NotificationChannels.Push),
        (NotificationEvents.ExportReady, NotificationChannels.Email),
    };

    /// <summary>Whether (event, channel) is notifiable by default (bottom of the resolution stack).</summary>
    public static bool IsOn(string eventType, string channel) => DefaultOn.Contains((eventType, channel));

    /// <summary>The default-on pairs, ordered for a stable <c>GET</c> response body.</summary>
    public static IReadOnlyList<(string Event, string Channel)> OnPairs =>
        DefaultOn
            .OrderBy(p => p.Event, StringComparer.Ordinal)
            .ThenBy(p => p.Channel, StringComparer.Ordinal)
            .ToList();
}

namespace Paybitch.Api.Features.Notifications.Push;

/// <summary>
/// A single visible push to one device (EXT-D5a). The wire payload carries IDS + a name-free
/// <c>loc-key</c> ONLY — never an amount, title, or name (§4.5 upheld literally). The client's
/// Notification Service Extension (<c>mutable-content:1</c>) hydrates the actor's first name, the group
/// name, and the amount ON DEVICE by fetching the entity over the authenticated channel.
/// </summary>
/// <param name="DeviceToken">The target APNs device token.</param>
/// <param name="Kind">The §3.10 verb, e.g. <c>expense.created</c> — routes the NSE's local fetch.</param>
/// <param name="GroupId">The group the event belongs to (an id, not a name).</param>
/// <param name="EntityId">The target entity id the NSE fetches, or null for a summary.</param>
/// <param name="LocKey">The generic, name-free localization key on the wire (e.g. <c>NOTIF_NEW_ACTIVITY</c>).</param>
/// <param name="Count">For a coalesced summary push: how many events collapsed (a number, not PII).</param>
public sealed record ApnsPush(
    string DeviceToken,
    string Kind,
    Guid GroupId,
    Guid? EntityId,
    string LocKey,
    int? Count = null);

/// <summary>
/// The APNs transport seam (EXT-D5a / §4.5). A DEV implementation logs; a PROD implementation talks
/// HTTP/2 to Apple with <c>.p8</c> token auth. Resolved OPTIONALLY by the fan-out worker (falls back to
/// <see cref="LogApnsSender"/> when unregistered), so a prod deployment swaps it in via DI without any
/// change to E5.
/// </summary>
public interface IApnsSender
{
    /// <summary>Deliver one push. Implementations MUST NOT put names/amounts on the wire (asserted by test, §5.8).</summary>
    Task SendAsync(ApnsPush push, CancellationToken ct = default);
}

/// <summary>The name-free localization keys the wire alert may carry (the NSE re-renders on device).</summary>
public static class ApnsLocKeys
{
    /// <summary>Single event: "New activity in a group".</summary>
    public const string NewActivity = "NOTIF_NEW_ACTIVITY";

    /// <summary>Coalesced burst: the NSE renders e.g. "3 new expenses in Chata 2026" from the custom <c>count</c>.</summary>
    public const string NewActivitySummary = "NOTIF_NEW_ACTIVITY_SUMMARY";
}

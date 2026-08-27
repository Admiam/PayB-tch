namespace Paybitch.Api.Features.Notifications;

// --- Requests ---

/// <summary>
/// <c>PUT /me/notification-prefs</c> body (§5.4). A full replace of the caller's override set. Over-post
/// guard (§5.7): binds ONLY these fields; <c>eventType</c>/<c>channel</c> are validated against their
/// closed sets, and each per-group row (<c>groupId != null</c>) runs a D6 membership check in the handler.
/// </summary>
public sealed record UpdateNotificationPrefsRequest(IReadOnlyList<NotificationPrefInput>? Prefs);

/// <summary>
/// One override the caller is setting. <c>groupId == null</c> ⇒ a GLOBAL override (all groups).
/// <c>eventType</c>/<c>channel</c> are nullable on the wire so a missing field fails with the specific
/// 422 twin (<c>unknown_event_type</c> / <c>invalid_channel</c>) rather than a bind error.
/// </summary>
public sealed record NotificationPrefInput(Guid? GroupId, string? EventType, string? Channel, bool Enabled);

// --- Responses ---

/// <summary>The prefs surface: aggregate <c>version</c> (ETag), the raw overrides, the default matrix, and a resolved view.</summary>
public sealed record NotificationPrefsResponse(
    int Version,
    IReadOnlyList<NotificationPrefRow> Overrides,
    IReadOnlyList<DefaultPrefRow> Defaults,
    IReadOnlyList<ResolvedPrefRow> Resolved);

/// <summary>A raw stored override row.</summary>
public sealed record NotificationPrefRow(Guid? GroupId, string EventType, string Channel, bool Enabled);

/// <summary>A default-on (event, channel) pair (everything not listed defaults off).</summary>
public sealed record DefaultPrefRow(string EventType, string Channel, bool Enabled);

/// <summary>An effective decision after per-group → global → default resolution, with its source.</summary>
public sealed record ResolvedPrefRow(Guid GroupId, string EventType, string Channel, bool Enabled, string Source);

namespace Paybitch.Infrastructure.Entities;

/// <summary>
/// E5 notification_prefs — per-user / per-group (NULL = global) / per-event / per-channel override.
/// Account-settings surface, not a synced group entity.
/// </summary>
public sealed class NotificationPref
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public Guid? GroupId { get; set; }                        // NULL = global override (all groups)
    public string EventType { get; set; } = string.Empty;    // a §3.10 activity verb
    public string Channel { get; set; } = string.Empty;      // 'push' | 'email'
    public bool Enabled { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

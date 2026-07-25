namespace Paybitch.Infrastructure.Entities;

/// <summary>E5 notification_cursor — server-internal fan-out watermark. Never synced, never user-visible.</summary>
public sealed class NotificationCursor
{
    public string Worker { get; set; } = string.Empty;       // logical worker id, PK (e.g. 'notification.fanout')
    public Guid LastActivityId { get; set; }                 // last activity_log.id dispatched
    public DateTimeOffset LastCreatedAt { get; set; }        // lower bound of the overlap re-scan
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

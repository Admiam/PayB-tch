namespace Paybitch.Infrastructure.Entities;

/// <summary>activity_log — append-only event feed. metadata carries ids + field NAMES only, never amounts.</summary>
public sealed class ActivityLogEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid GroupId { get; set; }
    public Guid? ActorUser { get; set; }
    public string Verb { get; set; } = string.Empty;           // closed taxonomy, e.g. 'expense.created'
    public string TargetType { get; set; } = string.Empty;     // 'expense'|'settlement'|'member'|'group'|'invite'
    public Guid? TargetId { get; set; }
    public string? Metadata { get; set; }                      // jsonb (raw)
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

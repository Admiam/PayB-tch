namespace Paybitch.Infrastructure.Entities;

/// <summary>
/// E0 jobs — the Postgres-backed, multi-instance-safe work queue (time-based + demand work).
/// Claimed via FOR UPDATE SKIP LOCKED; at-least-once; handlers must be idempotent.
/// </summary>
public sealed class Job : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Kind { get; set; } = string.Empty;          // 'recurring.materialize'|'export.build'|'blob.hard_delete'|...
    public string Payload { get; set; } = "{}";               // jsonb, ids only
    public string Status { get; set; } = "queued";            // 'queued'|'running'|'succeeded'|'failed'|'dead'
    public DateTimeOffset RunAt { get; set; } = DateTimeOffset.UtcNow;
    public int Priority { get; set; } = 100;                  // lower = sooner
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 8;
    public string? LockedBy { get; set; }
    public DateTimeOffset? LockedAt { get; set; }
    public string? LastError { get; set; }
    public string? DedupeKey { get; set; }                    // partial-unique while queued/running
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

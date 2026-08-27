namespace Paybitch.Infrastructure.Entities;

/// <summary>
/// change_log — the /sync delta feed. seq (bigserial) is the cursor. Written by application code
/// in the same transaction as every mutation; the 'access' entity_type is synthetic.
/// </summary>
public sealed class ChangeLogEntry
{
    public long Seq { get; set; }                              // bigserial PK (DB-generated)
    public Guid GroupId { get; set; }
    public string EntityType { get; set; } = string.Empty;    // see AppDbContext CHECK / ChangeLogWriter
    public Guid EntityId { get; set; }                        // for 'access' rows: the affected users.id
    public bool IsDelete { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;  // informational; NOT the prune key
}

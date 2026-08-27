namespace Paybitch.Infrastructure.Entities;

/// <summary>
/// E10a comments — a comment on an expense. author_user is a real account, never a ghost.
/// Full X1 sync contract: client_id + UNIQUE(group_id, client_id), version, soft-delete tombstone.
/// </summary>
public sealed class Comment : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid GroupId { get; set; }
    public Guid ExpenseId { get; set; }
    public Guid AuthorUser { get; set; }                     // FK users.id (RESTRICT); erase-scrub route
    public string? ClientId { get; set; }
    public string Body { get; set; } = string.Empty;        // 1..2000 chars
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;  // > CreatedAt ⇒ client renders "edited"
    public DateTimeOffset? DeletedAt { get; set; }
}

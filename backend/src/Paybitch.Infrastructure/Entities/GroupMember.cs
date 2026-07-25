namespace Paybitch.Infrastructure.Entities;

/// <summary>
/// group_members — a participant. user_id NULL = ghost (placeholder). Every money-bearing FK
/// points here, never at users.id.
/// </summary>
public sealed class GroupMember : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid GroupId { get; set; }
    public Guid? UserId { get; set; }                           // NULL = ghost
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = "member";               // 'owner' | 'admin' | 'member'
    public string? IconSymbol { get; set; }
    public string? ImageUrl { get; set; }                       // E7 avatars (reserved v1 col, versioned key)
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? FormerUserId { get; set; }                     // set on leave; D7 scrub route to unlinked rows
}

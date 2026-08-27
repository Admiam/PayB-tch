namespace Paybitch.Infrastructure.Entities;

/// <summary>invites — single-use hashed token; optional ghost member to claim.</summary>
public sealed class Invite
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid GroupId { get; set; }
    public byte[] TokenHash { get; set; } = Array.Empty<byte>();  // hash of a 128-bit single-use token; UNIQUE
    public string? Email { get; set; }                            // citext
    public Guid? MemberId { get; set; }                           // ghost to claim
    public Guid? InvitedBy { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

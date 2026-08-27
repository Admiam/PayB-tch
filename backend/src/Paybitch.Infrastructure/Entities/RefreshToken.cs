namespace Paybitch.Infrastructure.Entities;

/// <summary>refresh_tokens — rotation family with reuse detection.</summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public byte[] TokenHash { get; set; } = Array.Empty<byte>();  // SHA-256 of the opaque token; UNIQUE
    public Guid FamilyId { get; set; }
    public Guid? ReplacedBy { get; set; }                         // self-FK → refresh_tokens.id
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

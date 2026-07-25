using System.Net;

namespace Paybitch.Infrastructure.Entities;

/// <summary>
/// E6 email_login_tokens — one row per issued OTP / magic-link code. Single-use via consumed_at.
/// Not group-scoped, not synced. code_hash = SHA-256(pepper ‖ code).
/// </summary>
public sealed class EmailLoginToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Email { get; set; } = string.Empty;        // citext (lower-cased)
    public byte[] CodeHash { get; set; } = Array.Empty<byte>();
    public string Purpose { get; set; } = string.Empty;      // 'login' | 'link' | 'verify_change'
    public Guid? UserId { get; set; }                        // NULL for 'login' of a new address
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public int Attempts { get; set; }
    public IPAddress? CreatedIp { get; set; }                // inet; purged with the row
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

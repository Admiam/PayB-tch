namespace Paybitch.Infrastructure.Entities;

/// <summary>
/// E0 email_suppressions — hard deliverability signals only (bounce/complaint/manual).
/// Digest opt-out is NOT here (it lives in users.digest_opt_in), so it never drops transactional mail.
/// </summary>
public sealed class EmailSuppression
{
    public string Email { get; set; } = string.Empty;         // citext PK
    public string Reason { get; set; } = string.Empty;        // 'bounce' | 'complaint' | 'manual'
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

namespace Paybitch.Infrastructure.Entities;

/// <summary>users. GDPR delete = anonymize via <see cref="DeletedAt"/> soft-delete (D7).</summary>
public sealed class User : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }                          // citext, nullable; NOT unique (E6 drops UNIQUE)
    public string DefaultCurrency { get; set; } = "CZK";        // FK currencies.code
    public string Locale { get; set; } = "cs";
    public string? AvatarUrl { get; set; }                      // E7 avatars: holds a versioned storage key
    public int TokenEpoch { get; set; }
    public DateTimeOffset? EmailVerifiedAt { get; set; }        // E6
    public bool DigestOptIn { get; set; }                       // E5 (opt-in default false)
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
}

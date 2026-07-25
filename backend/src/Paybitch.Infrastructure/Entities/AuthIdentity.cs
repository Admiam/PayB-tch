namespace Paybitch.Infrastructure.Entities;

/// <summary>auth_identities. UNIQUE(provider, subject) is the global identity authority.</summary>
public sealed class AuthIdentity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public string Provider { get; set; } = string.Empty;        // 'apple' | 'email'
    public string Subject { get; set; } = string.Empty;         // Apple 'sub' / normalized email
    public byte[]? AppleRefreshTokenEnc { get; set; }           // encrypted at rest
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

namespace Paybitch.Infrastructure.Entities;

/// <summary>settlements. Per-currency only (D5): currency = the debt's currency. Void = soft-delete.</summary>
public sealed class Settlement : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid GroupId { get; set; }
    public string? ClientId { get; set; }                       // (group_id, client_id) idempotency (D9)
    public Guid FromMember { get; set; }                        // FK group_members.id
    public Guid ToMember { get; set; }                          // FK group_members.id (<> from_member)
    public long AmountMinor { get; set; }                       // integer minor units (D1)
    public string Currency { get; set; } = string.Empty;        // FK currencies.code
    public string? Method { get; set; }
    public DateOnly SettledOn { get; set; }
    public string? Notes { get; set; }
    public int Version { get; set; } = 1;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
}

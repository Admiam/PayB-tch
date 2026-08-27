namespace Paybitch.Infrastructure.Entities;

/// <summary>groups. archived_at = reversible archive; deleted_at = pure tombstone (no v1 delete path).</summary>
public sealed class Group : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = string.Empty;
    public string DefaultCurrency { get; set; } = "CZK";        // FK currencies.code
    public string? IconSymbol { get; set; }
    public string? ImageUrl { get; set; }                       // E7 avatars (added column)
    public int Version { get; set; } = 1;                       // optimistic concurrency (D8)
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ArchivedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

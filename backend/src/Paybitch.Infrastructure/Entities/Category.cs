namespace Paybitch.Infrastructure.Entities;

/// <summary>categories. group_id NULL = global preset (fixed-UUID seed). Soft-deletable.</summary>
public sealed class Category : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? GroupId { get; set; }                          // NULL = global preset
    public string Name { get; set; } = string.Empty;
    public string? IconSymbol { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
}

namespace Paybitch.Infrastructure.Entities;

/// <summary>
/// Marker for entities carrying created/updated timestamps. The AppDbContext SaveChanges
/// override stamps <see cref="UpdatedAt"/> = UtcNow on Added/Modified rows. Version columns
/// are NOT touched here — handlers own optimistic concurrency (D8).
/// </summary>
public interface IHasTimestamps
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
}

namespace Paybitch.Infrastructure.Entities;

/// <summary>devices — CLIENT-generated stable id (no server default). APNs token upsert target.</summary>
public sealed class Device : IHasTimestamps
{
    public Guid Id { get; set; }                                // CLIENT-generated; not auto-defaulted
    public Guid UserId { get; set; }
    public string ApnsToken { get; set; } = string.Empty;
    public string Platform { get; set; } = "ios";
    public string Kind { get; set; } = "apns";                  // E5: 'apns' | 'apns_live_activity'
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

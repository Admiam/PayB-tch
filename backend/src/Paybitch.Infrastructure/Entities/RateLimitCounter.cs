namespace Paybitch.Infrastructure.Entities;

/// <summary>
/// E8 rate_limit_counters — fixed-window distributed limiter store. Materialized only when a 2nd
/// instance is provisioned. Disposable op state (X2 n/a). Composite PK (partition_hash, window_start).
/// </summary>
public sealed class RateLimitCounter
{
    public byte[] PartitionHash { get; set; } = Array.Empty<byte>();  // HMAC of 'surface:dimension:value' (hashed at rest, X6)
    public DateTimeOffset WindowStart { get; set; }                   // floor(now(), window)
    public int Count { get; set; }
}

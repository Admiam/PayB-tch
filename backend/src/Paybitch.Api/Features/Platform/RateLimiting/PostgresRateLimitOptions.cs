namespace Paybitch.Api.Features.Platform.RateLimiting;

/// <summary>
/// Tunables for the E8 multi-instance rate limiter (<see cref="PostgresRateLimitStore"/>), bound from the
/// <c>RateLimiting</c> configuration section. Only consulted once the Postgres limiter is swapped in via
/// <see cref="PostgresRateLimitingRegistration.AddPostgresRateLimiting"/> (EXT-D8b); the v1 in-memory
/// limiter ignores this section entirely.
/// </summary>
public sealed class PostgresRateLimitOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>
    /// Secret used to HMAC-hash every partition key at rest (<c>rate_limit_counters.partition_hash</c>, X6/§8.6)
    /// so the table is never a plaintext log of IPs / Apple <c>sub</c>s / user ids. It MUST be identical across
    /// every app instance (else partitions never collide and the shared cap silently splits, defeating EXT-D8b)
    /// and SHOULD be a 32+ char random secret supplied out-of-band (<c>RateLimiting__PartitionHmacKey</c> env,
    /// never in the compose file). Empty ⇒ the swap-in fails fast at boot (validator below).
    /// </summary>
    public string PartitionHmacKey { get; set; } = string.Empty;

    /// <summary>
    /// The GC job (<c>ratelimit.gc</c>) prunes windows older than this. MUST exceed the widest Appendix A
    /// window (the per-day provisioning / invite-create caps ⇒ 1 day) so an in-flight daily window is never
    /// deleted out from under a live counter. A stale row is harmless (X5-idempotent), so err generous.
    /// </summary>
    public TimeSpan GcRetention { get; set; } = TimeSpan.FromDays(2);

    /// <summary>How often <see cref="RateLimitGcScheduler"/> enqueues a <c>ratelimit.gc</c> job (dedupe-collapsed).</summary>
    public TimeSpan GcInterval { get; set; } = TimeSpan.FromHours(6);
}

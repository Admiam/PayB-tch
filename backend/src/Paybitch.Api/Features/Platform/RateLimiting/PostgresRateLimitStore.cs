using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Paybitch.Infrastructure.Abstractions;

namespace Paybitch.Api.Features.Platform.RateLimiting;

/// <summary>
/// The distributed, multi-instance fixed-window rate-limit counter store (EXT-D8b/c, §8.3). It replaces the
/// v1 in-memory <c>System.Threading.RateLimiting</c> limiter (§4.3) — which is per-instance and therefore
/// silently doubles every Appendix A cap the moment a second instance boots — with a single atomic
/// <c>INSERT … ON CONFLICT … DO UPDATE … RETURNING count</c> against <c>rate_limit_counters</c>, the DB you
/// already run (no second stateful service, no new backup, nothing extra to carry across the Hetzner exit).
/// </summary>
/// <remarks>
/// <para>
/// <b>Correctness.</b> Fixed-window (KISS over sliding-log, §8.3): the row for
/// <c>floor(now, window)</c> is incremented and the post-increment count is tested against the limit in one
/// round trip, so N instances contending on the same partition serialize on the composite PK and observe a
/// single monotone counter — the whole point of EXT-D8b (two instances enforce the <i>same aggregate</i> cap
/// one did). The boundary-burst weakness (up to 2× across a window edge) is immaterial at auth/invite limits.
/// </para>
/// <para>
/// <b>Privacy (X6/§8.6).</b> The partition key <c>surface:dimension:value</c> — e.g. <c>auth:ip:203.0.113.7</c>
/// — is HMAC-SHA256'd with <see cref="PostgresRateLimitOptions.PartitionHmacKey"/> before it touches the DB,
/// so the table is not a plaintext IP/PII log. The caller MUST pass a <b>trusted-proxy-resolved</b> value
/// (post-<c>UseForwardedHeaders</c>, §4.3); a spoofable key is <i>worse</i> shared, because one poisoned
/// partition now degrades every instance.
/// </para>
/// <para>
/// <b>Failure mode.</b> The limiter <b>fails open</b>: a transient DB error (or a missing HMAC key) logs a
/// warning and admits the request rather than 503-ing the site over a rate-counter blip. The API needs the
/// same Postgres for auth anyway, so a hard DB outage already blocks brute force at the source.
/// </para>
/// <para>
/// Registered as a <b>singleton</b> (no shared mutable state; a fresh pooled <see cref="NpgsqlConnection"/>
/// per call) and consumed by <see cref="PostgresPartitionRateLimiter"/> when the swap-in is active. Windows
/// are pruned by the <c>ratelimit.gc</c> job (<see cref="RateLimitGcHandler"/>).
/// </para>
/// </remarks>
public sealed class PostgresRateLimitStore
{
    private const string DevConnectionString =
        "Host=localhost;Port=5432;Database=paybitch;Username=paybitch;Password=paybitch";

    // One statement, one round trip. RETURNING is the post-increment count → reject when it exceeds the limit.
    private const string UpsertSql = """
        INSERT INTO rate_limit_counters (partition_hash, window_start, count)
        VALUES (@ph, @w, 1)
        ON CONFLICT (partition_hash, window_start)
        DO UPDATE SET count = rate_limit_counters.count + 1
        RETURNING count;
        """;

    private readonly string _connectionString;
    private readonly byte[]? _hmacKey;
    private readonly IClock _clock;
    private readonly ILogger<PostgresRateLimitStore> _logger;

    public PostgresRateLimitStore(
        IConfiguration config,
        IOptions<PostgresRateLimitOptions> options,
        IClock clock,
        ILogger<PostgresRateLimitStore> logger)
    {
        _clock = clock;
        _logger = logger;

        _connectionString =
            config.GetConnectionString("Postgres")
            ?? Environment.GetEnvironmentVariable("PAYBITCH_DB")
            ?? DevConnectionString;

        var key = options.Value.PartitionHmacKey;
        _hmacKey = string.IsNullOrWhiteSpace(key) ? null : Encoding.UTF8.GetBytes(key);
    }

    /// <summary>
    /// Atomically increment the counter for <paramref name="partitionKey"/> in the current window and decide.
    /// <paramref name="partitionKey"/> is the raw <c>surface:dimension:value</c> string (hashed here, never
    /// persisted in the clear). Fails open on any store error.
    /// </summary>
    public async Task<RateLimitDecision> AcquireAsync(
        string partitionKey, int permitLimit, TimeSpan window, CancellationToken ct)
    {
        if (_hmacKey is null)
        {
            // Misconfiguration (swap-in without a key). Boot validation should have caught this; fail open loudly.
            _logger.LogError("PostgresRateLimitStore has no PartitionHmacKey configured; admitting request (fail-open).");
            return RateLimitDecision.Allowed;
        }

        var now = _clock.UtcNow;
        var windowStart = FloorToWindow(now, window);
        var hash = HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(partitionKey));

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                UpsertSql, new { ph = hash, w = windowStart }, cancellationToken: ct));

            if (count <= permitLimit)
                return RateLimitDecision.Allowed;

            var retryAfter = windowStart + window - now;
            return RateLimitDecision.Rejected(retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "PostgresRateLimitStore upsert failed; admitting request (fail-open).");
            return RateLimitDecision.Allowed;
        }
    }

    /// <summary>floor(<paramref name="instant"/>, <paramref name="window"/>) as a UTC instant — one row per window.</summary>
    internal static DateTimeOffset FloorToWindow(DateTimeOffset instant, TimeSpan window)
    {
        var ticks = instant.UtcDateTime.Ticks;
        return new DateTimeOffset(ticks - (ticks % window.Ticks), TimeSpan.Zero);
    }
}

/// <summary>Outcome of an <see cref="PostgresRateLimitStore.AcquireAsync"/> — allow, or reject with a <c>Retry-After</c>.</summary>
public readonly record struct RateLimitDecision(bool IsAllowed, TimeSpan? RetryAfter)
{
    public static readonly RateLimitDecision Allowed = new(true, null);
    public static RateLimitDecision Rejected(TimeSpan retryAfter) => new(false, retryAfter);
}

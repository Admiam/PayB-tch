using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paybitch.Api.Features.Platform.Jobs;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;

namespace Paybitch.Api.Features.Platform.RateLimiting;

/// <summary>
/// E0 job handler that prunes expired <c>rate_limit_counters</c> windows (§8.3). Fixed-window rows are
/// disposable operational state (X2): once a window has fully elapsed its row can never admit or reject
/// another request, so a background GC keeps the table bounded. Enqueued on a cadence by
/// <see cref="RateLimitGcScheduler"/> (dedupe-collapsed across instances), it is a plain bulk
/// <c>DELETE … WHERE window_start &lt; threshold</c> — idempotent and safe to re-run (a second pass just
/// deletes nothing, X5).
/// </summary>
/// <remarks>
/// Auto-discovered as an <see cref="IJobHandler"/> by <c>AddPlatform</c>. It depends only on always-present
/// services (<see cref="AppDbContext"/>, <see cref="IClock"/>, options) so the handler enumeration in
/// <c>JobRunner</c> never breaks in a single-instance deployment that has not swapped in the Postgres
/// limiter — the job is simply never enqueued there. The retention threshold
/// (<see cref="PostgresRateLimitOptions.GcRetention"/>) intentionally exceeds the widest Appendix A window.
/// </remarks>
public sealed class RateLimitGcHandler(
    AppDbContext db,
    IClock clock,
    IOptions<PostgresRateLimitOptions> options,
    ILogger<RateLimitGcHandler> logger) : IJobHandler
{
    public const string JobKind = "ratelimit.gc";

    public string Kind => JobKind;

    public async Task HandleAsync(JobContext job, CancellationToken ct)
    {
        var threshold = clock.UtcNow - options.Value.GcRetention;
        var deleted = await db.RateLimitCounters
            .Where(c => c.WindowStart < threshold)
            .ExecuteDeleteAsync(ct);

        if (deleted > 0)
            logger.LogInformation("ratelimit.gc pruned {Deleted} expired rate-limit windows (< {Threshold:o})",
                deleted, threshold);
    }
}

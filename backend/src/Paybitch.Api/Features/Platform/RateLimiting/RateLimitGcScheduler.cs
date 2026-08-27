using Microsoft.Extensions.Options;
using Paybitch.Api.Features.Platform.Jobs;

namespace Paybitch.Api.Features.Platform.RateLimiting;

/// <summary>
/// Seeds the <c>ratelimit.gc</c> job on a cadence (<see cref="PostgresRateLimitOptions.GcInterval"/>). Kept
/// deliberately thin: the durable work and its at-least-once retry live in the E0 jobs substrate — this only
/// enqueues, with a stable <c>dedupeKey</c> so N instances (and a restart) collapse to a single in-flight GC
/// row (X5, the <c>uq_jobs_dedupe</c> partial-unique index is the backstop). Registered <b>only</b> by
/// <see cref="PostgresRateLimitingRegistration.AddPostgresRateLimiting"/>, so a single-instance deployment
/// that never swaps in the Postgres limiter never runs it and the counter table stays empty.
/// </summary>
public sealed class RateLimitGcScheduler(
    IServiceScopeFactory scopeFactory,
    IOptions<PostgresRateLimitOptions> options,
    ILogger<RateLimitGcScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.GcInterval;
        logger.LogInformation("RateLimitGcScheduler started (interval {Interval})", interval);

        // Seed one immediately, then on the interval. Enqueue is dedupe-guarded, so a still-pending GC no-ops.
        await EnqueueAsync(stoppingToken);

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await EnqueueAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task EnqueueAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            await queue.EnqueueAsync(RateLimitGcHandler.JobKind, new { }, dedupeKey: RateLimitGcHandler.JobKind, ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A blip enqueuing the GC job must never kill the scheduler loop; the next tick retries.
            logger.LogWarning(ex, "RateLimitGcScheduler failed to enqueue ratelimit.gc; will retry next tick");
        }
    }
}

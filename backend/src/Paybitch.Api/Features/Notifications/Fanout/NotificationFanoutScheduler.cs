using Microsoft.Extensions.Hosting;
using Paybitch.Api.Features.Platform.Jobs;

namespace Paybitch.Api.Features.Notifications.Fanout;

/// <summary>
/// Ticks the <c>notification.fanout</c> worker (EXT-D5e). The ACTUAL work (cursor scan + dispatch) runs
/// on the E0 jobs substrate via <see cref="NotificationFanoutHandler"/> so it is multi-instance-safe;
/// this scheduler just keeps exactly one fan-out job in flight, enqueuing on a fixed cadence with the
/// dedupe key <c>notification.fanout</c> (a concurrent/duplicate enqueue collapses to one, and a queued
/// or running job suppresses the next enqueue). This both SEEDS the loop on startup and self-heals it.
/// </summary>
/// <remarks>
/// CROSS-CUTTING: hosted services are not assembly-scanned, so this needs one line in <c>Program.cs</c>:
/// <c>builder.Services.AddHostedService&lt;NotificationFanoutScheduler&gt;();</c> (see convergence notes).
/// </remarks>
public sealed class NotificationFanoutScheduler(
    IServiceScopeFactory scopeFactory,
    ILogger<NotificationFanoutScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("NotificationFanoutScheduler started (tick {Interval})", TickInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnqueueTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "NotificationFanoutScheduler tick failed");
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("NotificationFanoutScheduler stopping");
    }

    private async Task EnqueueTickAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var jobQueue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
        await jobQueue.EnqueueAsync(
            NotificationJobKinds.Fanout, new { }, dedupeKey: NotificationWorkers.Fanout, runAt: null, ct);
    }
}

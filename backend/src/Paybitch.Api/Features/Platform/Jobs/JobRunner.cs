using System.Data.Common;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;

namespace Paybitch.Api.Features.Platform.Jobs;

/// <summary>
/// The single-loop E0 job drain (§0.4.2), safe to run on every app instance. Each tick atomically
/// claims one due job with <c>FOR UPDATE SKIP LOCKED</c> (so N instances coordinate through Postgres
/// with zero locks of their own), dispatches it to the matching <see cref="IJobHandler"/>, and marks
/// the outcome:
/// <list type="bullet">
///   <item>success ⇒ <c>succeeded</c>;</item>
///   <item>exception ⇒ backoff-reschedule (<c>queued</c>, <c>run_at = now + backoff</c>), or, once
///   <c>attempts ≥ max_attempts</c>, <c>dead</c> (poison queue, EXT-D0b);</item>
///   <item>a stranded lease (worker killed mid-run) is folded into the same claim query and reclaimed
///   once <c>locked_at</c> is older than the lease — restoring at-least-once across a deploy (EXT-D0h).</item>
/// </list>
/// The claim increments <c>attempts</c>, so even a reclaim path terminates a genuinely poisonous job at
/// <c>max_attempts</c> instead of looping forever.
/// </summary>
public sealed class JobRunner : BackgroundService
{
    private const int MaxBackoffExponent = 16;   // 2^16 · base already far exceeds any sane cap
    private const int MaxErrorLength = 500;       // last_error is truncated: never a full stack, no PII

    // One UPDATE … RETURNING per tick — no read-then-write race. The second predicate folds stale-lease
    // reclaim (EXT-D0h) into the same statement, so there is no separate sweep to schedule.
    private const string ClaimSql = """
        UPDATE jobs SET
            status     = 'running',
            locked_by  = @Instance,
            locked_at  = now(),
            attempts   = attempts + 1,
            updated_at = now()
        WHERE id = (
            SELECT id FROM jobs
            WHERE (status = 'queued'  AND run_at <= now())
               OR (status = 'running' AND locked_at < now() - @Lease)
            ORDER BY priority, run_at
            FOR UPDATE SKIP LOCKED
            LIMIT 1
        )
        RETURNING id           AS "Id",
                  kind         AS "Kind",
                  payload      AS "Payload",
                  attempts     AS "Attempts",
                  max_attempts AS "MaxAttempts";
        """;

    private const string MarkSucceededSql = """
        UPDATE jobs SET status = 'succeeded', locked_by = NULL, locked_at = NULL, updated_at = now()
        WHERE id = @Id;
        """;

    // attempts was already incremented at claim, so this row's attempts is this run's count.
    private const string MarkFailedSql = """
        UPDATE jobs SET
            status     = CASE WHEN attempts >= max_attempts THEN 'dead' ELSE 'queued' END,
            run_at     = CASE WHEN attempts >= max_attempts THEN run_at ELSE now() + @Backoff END,
            last_error = @Error,
            locked_by  = NULL,
            locked_at  = NULL,
            updated_at = now()
        WHERE id = @Id;
        """;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;
    private readonly ILogger<JobRunner> _logger;
    private readonly JobRunnerOptions _options;
    private readonly string _instanceId = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    private DateTimeOffset _nextGaugeLog = DateTimeOffset.MinValue;

    public JobRunner(
        IServiceScopeFactory scopeFactory,
        IOptions<JobRunnerOptions> options,
        IClock clock,
        ILogger<JobRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("JobRunner started (instance {Instance})", _instanceId);

        while (!stoppingToken.IsCancellationRequested)
        {
            var claimedWork = false;
            try
            {
                claimedWork = await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A tick failure (transient DB error, etc.) must never kill the loop.
                _logger.LogError(ex, "JobRunner tick failed");
            }

            var delay = Jitter(claimedWork ? _options.BusyPollInterval : _options.IdlePollInterval);
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("JobRunner stopping (instance {Instance})", _instanceId);
    }

    /// <summary>Claim + dispatch at most one job. Returns true when a job was claimed (busy cadence).</summary>
    private async Task<bool> TickAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conn = db.Database.GetDbConnection();

        var job = await ClaimAsync(conn, ct);
        if (job is not null)
            await ProcessAsync(scope.ServiceProvider, conn, job, ct);

        await MaybeLogGaugesAsync(db, ct);
        return job is not null;
    }

    private Task<ClaimedJob?> ClaimAsync(DbConnection conn, CancellationToken ct) =>
        conn.QueryFirstOrDefaultAsync<ClaimedJob>(new CommandDefinition(
            ClaimSql,
            new { Instance = _instanceId, Lease = _options.LeaseTimeout },
            cancellationToken: ct));

    private async Task ProcessAsync(IServiceProvider sp, DbConnection conn, ClaimedJob job, CancellationToken ct)
    {
        var handler = sp.GetServices<IJobHandler>().FirstOrDefault(h => h.Kind == job.Kind);
        if (handler is null)
        {
            // Unknown kind: treat as a failure so it backs off (no tight spin) and dead-letters at
            // max_attempts, becoming an alert signal rather than a silent loss.
            _logger.LogError("No IJobHandler registered for job kind {Kind} (job {JobId}); failing", job.Kind, job.Id);
            await MarkFailedAsync(conn, job, $"no handler for kind '{job.Kind}'", ct);
            return;
        }

        try
        {
            var context = new JobContext(job.Id, job.Kind, job.Payload, job.Attempts);
            await handler.HandleAsync(context, ct);
            await MarkSucceededAsync(conn, job.Id, ct);
            _logger.LogInformation(
                "Job {JobId} ({Kind}) succeeded on attempt {Attempt}", job.Id, job.Kind, job.Attempts);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown mid-run: leave the row 'running' so the lease reaper reclaims it (EXT-D0h). Rethrow
            // so the loop unwinds cleanly rather than dead-lettering a job that never really failed.
            throw;
        }
        catch (Exception ex)
        {
            var deadLetter = job.Attempts >= job.MaxAttempts;
            _logger.Log(
                deadLetter ? LogLevel.Error : LogLevel.Warning, ex,
                "Job {JobId} ({Kind}) failed on attempt {Attempt}/{Max}{DeadLetter}",
                job.Id, job.Kind, job.Attempts, job.MaxAttempts, deadLetter ? " → dead-letter" : string.Empty);
            await MarkFailedAsync(conn, job, Describe(ex), ct);
        }
    }

    private static Task MarkSucceededAsync(DbConnection conn, Guid id, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition(MarkSucceededSql, new { Id = id }, cancellationToken: ct));

    private Task MarkFailedAsync(DbConnection conn, ClaimedJob job, string error, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition(
            MarkFailedSql,
            new { job.Id, Backoff = Backoff(job.Attempts), Error = error },
            cancellationToken: ct));

    /// <summary>Queue-health gauges (EXT-D0d): the drain canary plus the stale-lease blind spot.</summary>
    private async Task MaybeLogGaugesAsync(AppDbContext db, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        if (now < _nextGaugeLog)
            return;
        _nextGaugeLog = now + _options.GaugeLogInterval;

        var oldestQueued = await db.Jobs
            .Where(j => j.Status == JobStatus.Queued && j.RunAt <= now)
            .MinAsync(j => (DateTimeOffset?)j.RunAt, ct);
        var deadCount = await db.Jobs.CountAsync(j => j.Status == JobStatus.Dead, ct);
        var leaseThreshold = now - _options.LeaseTimeout;
        var runningOverLease = await db.Jobs
            .CountAsync(j => j.Status == JobStatus.Running && j.LockedAt < leaseThreshold, ct);

        var oldestAgeSeconds = oldestQueued is null ? 0d : (now - oldestQueued.Value).TotalSeconds;
        _logger.LogInformation(
            "Job queue gauges: oldest_queued_age_seconds={Age} dead={Dead} running_over_lease={OverLease}",
            oldestAgeSeconds, deadCount, runningOverLease);
    }

    private TimeSpan Backoff(int attempts)
    {
        var exponent = Math.Min(attempts, MaxBackoffExponent);
        var seconds = Math.Pow(2, exponent) * _options.BackoffBase.TotalSeconds;
        var capped = Math.Min(seconds, _options.BackoffCap.TotalSeconds);
        return TimeSpan.FromSeconds(ApplyJitter(capped));
    }

    private TimeSpan Jitter(TimeSpan interval) => TimeSpan.FromMilliseconds(ApplyJitter(interval.TotalMilliseconds));

    private double ApplyJitter(double value)
    {
        var swing = 1 + ((Random.Shared.NextDouble() * 2) - 1) * _options.JitterFraction;
        return value * swing;
    }

    private static string Describe(Exception ex)
    {
        var text = $"{ex.GetType().Name}: {ex.Message}";
        return text.Length <= MaxErrorLength ? text : text[..MaxErrorLength];
    }

    /// <summary>Dapper projection of a claimed row (RETURNING columns aliased to PascalCase).</summary>
    private sealed record ClaimedJob(Guid Id, string Kind, string Payload, int Attempts, int MaxAttempts);
}

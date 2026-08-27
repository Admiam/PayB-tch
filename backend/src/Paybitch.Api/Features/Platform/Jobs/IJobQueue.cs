using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Platform.Jobs;

/// <summary>
/// The write-side of the E0 jobs substrate (§0.4.2): enqueue durable, at-least-once work drained by
/// <see cref="JobRunner"/>. Covers both time-based (scheduled via <paramref name="runAt"/>) and
/// demand work through one <c>run_at</c> column.
/// </summary>
public interface IJobQueue
{
    /// <summary>
    /// Enqueue a job of <paramref name="kind"/> carrying <paramref name="payload"/> (serialized to JSON —
    /// ids only, no money/PII beyond ids, §4.3). Runs at <paramref name="runAt"/> or immediately when null.
    /// </summary>
    Task EnqueueAsync(string kind, object payload, DateTimeOffset? runAt = null, CancellationToken ct = default);

    /// <summary>
    /// Dedup-aware enqueue (X5). A non-null <paramref name="dedupeKey"/> is a no-op when a job with that
    /// key is already <c>queued</c>/<c>running</c> — the partial-unique <c>uq_jobs_dedupe</c> index is the
    /// backstop, so a concurrent double-enqueue collapses to one row instead of throwing.
    /// </summary>
    Task EnqueueAsync(string kind, object payload, string? dedupeKey, DateTimeOffset? runAt = null,
        CancellationToken ct = default);
}

/// <summary>EF-backed <see cref="IJobQueue"/>. Scoped: shares the request/handler <see cref="AppDbContext"/>.</summary>
public sealed class JobQueue(AppDbContext db, IClock clock, ILogger<JobQueue> logger) : IJobQueue
{
    public Task EnqueueAsync(string kind, object payload, DateTimeOffset? runAt = null,
        CancellationToken ct = default) =>
        EnqueueAsync(kind, payload, dedupeKey: null, runAt, ct);

    public async Task EnqueueAsync(string kind, object payload, string? dedupeKey,
        DateTimeOffset? runAt = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(payload);

        // Cheap fast-path: skip the insert entirely when a live job already holds this dedupe key.
        if (dedupeKey is not null &&
            await db.Jobs.AnyAsync(
                j => j.DedupeKey == dedupeKey && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running),
                ct))
        {
            logger.LogDebug("Job enqueue de-duplicated on key {DedupeKey} (kind {Kind})", dedupeKey, kind);
            return;
        }

        var job = new Job
        {
            Kind = kind,
            Payload = JsonSerializer.Serialize(payload),
            Status = JobStatus.Queued,
            RunAt = runAt ?? clock.UtcNow,
            DedupeKey = dedupeKey,
        };
        db.Jobs.Add(job);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (dedupeKey is not null && IsDedupeCollision(ex))
        {
            // A concurrent enqueue won the race to the same dedupe key — the partial-unique index did its
            // job (X5). Detach our losing insert and treat the enqueue as the intended no-op.
            db.Entry(job).State = EntityState.Detached;
            logger.LogDebug("Job enqueue lost dedupe race on key {DedupeKey} (kind {Kind})", dedupeKey, kind);
        }
    }

    /// <summary>True when the update failed on the <c>uq_jobs_dedupe</c> unique index (SQLSTATE 23505).</summary>
    private static bool IsDedupeCollision(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}

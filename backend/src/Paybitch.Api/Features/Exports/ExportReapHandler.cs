using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Paybitch.Api.Features.Platform.Jobs;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Exports;

/// <summary>
/// The <c>export.reap</c> job (X4, §4.6): the TTL lifecycle for one artifact. Scheduled with
/// <c>runAt = expires_at</c> at enqueue time, it flips the <c>export_results</c> row to <c>expired</c> and
/// drives the <b>two-phase</b> blob reclaim — a <c>blob_deletions</c> tombstone (reason <c>orphan</c>) plus
/// a <c>blob.hard_delete</c> job (§0.4.3). An export snapshot is a full copy of a user's/group's data, so
/// it must not outlive its short TTL (its retention deliberately diverges from the ledger).
/// </summary>
/// <remarks>
/// At-least-once + idempotent (X5): the <c>blob_deletions</c> PK (the storage key) and the
/// <c>blob.hard_delete</c> dedupe key both collapse re-runs, and an already-<c>expired</c> row short-circuits.
/// If the job fires before the TTL is actually due it re-schedules itself for the remaining interval rather
/// than reaping early.
/// </remarks>
public sealed class ExportReapHandler(
    AppDbContext db,
    IJobQueue jobQueue,
    IClock clock,
    ILogger<ExportReapHandler> logger) : IJobHandler
{
    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNameCaseInsensitive = true };

    public string Kind => ExportJobKinds.Reap;

    public async Task HandleAsync(JobContext job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<ReapPayload>(job.Payload, PayloadOptions);
        if (payload is null || payload.ExportResultId == Guid.Empty)
            throw new InvalidOperationException($"export.reap job {job.Id} has no valid exportResultId.");

        var row = await db.ExportResults.FirstOrDefaultAsync(r => r.Id == payload.ExportResultId, ct);
        if (row is null)
            return; // row already hard-deleted (e.g. GDPR anonymize, §4.6) — nothing to reap

        var now = clock.UtcNow;
        if (row.ExpiresAt > now)
        {
            // Not due yet — re-arm for the remaining interval instead of reaping a still-live artifact.
            await jobQueue.EnqueueAsync(
                ExportJobKinds.Reap, new { exportResultId = row.Id }, dedupeKey: null, runAt: row.ExpiresAt, ct: ct);
            return;
        }

        if (row.Status != "expired")
        {
            row.Status = "expired";
            row.CompletedAt ??= now;
        }

        // Two-phase blob reclaim (X4). The object key is the row id (Guid.ToString()); blob_deletions.PK is
        // that same Guid, so this is idempotent — a second reap finds the tombstone and skips the enqueue.
        if (!string.IsNullOrEmpty(row.StorageKey))
        {
            var storageKey = row.Id;
            var alreadyTombstoned = await db.BlobDeletions.AnyAsync(b => b.StorageKey == storageKey, ct);
            if (!alreadyTombstoned)
            {
                db.BlobDeletions.Add(new BlobDeletion
                {
                    StorageKey = storageKey,
                    Reason = ExportOperational.TtlReclaimReason,
                    RequestedAt = now,
                });
                await db.SaveChangesAsync(ct);

                await jobQueue.EnqueueAsync(
                    JobKinds.BlobHardDelete,
                    new { storageKey },
                    dedupeKey: $"{JobKinds.BlobHardDelete}:{storageKey}",
                    runAt: null,
                    ct: ct);
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("export.reap: expired artifact {Id} and enqueued blob reclaim", row.Id);
    }

    private sealed record ReapPayload(
        [property: JsonPropertyName("exportResultId")] Guid ExportResultId);
}

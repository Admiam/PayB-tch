using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Paybitch.Api.Features.Platform.Jobs;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;

namespace Paybitch.Api.Features.Platform.Blob;

/// <summary>
/// Drains the second phase of a two-phase blob deletion (X4, §0.4.3). Phase one — a
/// <c>blob_deletions</c> row plus a <c>blob.hard_delete</c> job — is written by the extension that
/// tombstones the reference (E1/E7 attachment purge, avatar replace, GDPR erase). This handler reclaims
/// the object: <see cref="IBlobStore.DeleteAsync"/> (idempotent), a HEAD-style
/// <see cref="IBlobStore.ExistsAsync"/> verify, then it stamps <c>blob_deletions.confirmed_at</c>.
/// Idempotent and safe to re-run: an already-confirmed or already-gone key is a no-op.
/// </summary>
/// <remarks>
/// Payload contract (ids only, §4.3): <c>{ "storageKey": "&lt;guid&gt;" }</c>. The blob object key is the
/// <see cref="Paybitch.Infrastructure.Entities.BlobDeletion.StorageKey"/> Guid rendered via
/// <c>ToString()</c> — the same rendering the uploader (E1/E7) uses when it PUTs the object.
/// </remarks>
public sealed class BlobHardDeleteHandler(
    IBlobStore blobStore,
    AppDbContext db,
    IClock clock,
    ILogger<BlobHardDeleteHandler> logger) : IJobHandler
{
    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNameCaseInsensitive = true };

    public string Kind => JobKinds.BlobHardDelete;

    public async Task HandleAsync(JobContext job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<BlobHardDeletePayload>(job.Payload, PayloadOptions);
        if (payload is null || payload.StorageKey == Guid.Empty)
            throw new InvalidOperationException($"blob.hard_delete job {job.Id} has no valid storageKey.");

        var storageKey = payload.StorageKey;
        var row = await db.BlobDeletions.FirstOrDefaultAsync(b => b.StorageKey == storageKey, ct);
        if (row is null)
        {
            // No ledger row: the reference was never tombstoned or already fully reclaimed. Nothing to do.
            logger.LogInformation("blob.hard_delete: no blob_deletions row for {StorageKey}; treating as done", storageKey);
            return;
        }

        if (row.ConfirmedAt is not null)
            return;   // already reclaimed by a prior run (idempotent)

        var objectKey = storageKey.ToString();
        row.Attempts += 1;
        await blobStore.DeleteAsync(objectKey, ct);

        if (await blobStore.ExistsAsync(objectKey, ct))
        {
            // Delete didn't stick (e.g. eventual-consistency window): persist the attempt bump and throw so
            // the job backs off and retries. A genuinely stuck key eventually dead-letters at max_attempts.
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException($"Blob {objectKey} still present after delete.");
        }

        row.ConfirmedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("blob.hard_delete: reclaimed {StorageKey} (reason {Reason})", storageKey, row.Reason);
    }

    /// <summary>Job payload: the <c>blob_deletions</c> PK to reclaim.</summary>
    private sealed record BlobHardDeletePayload(
        [property: JsonPropertyName("storageKey")] Guid StorageKey);
}

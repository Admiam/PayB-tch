using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Paybitch.Api.Features.Me;
using Paybitch.Api.Features.Platform.Blob;
using Paybitch.Api.Features.Platform.Jobs;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Exports;

/// <summary>
/// The <c>export.build</c> job (EXT-D4a/d, §0.4.2): render a group CSV/PDF or a GDPR JSON dump, store the
/// bytes in <see cref="IBlobStore"/> (X4 — referenced, never owned by Postgres), and flip the
/// <c>export_results</c> row to <c>ready</c> with its opaque key + size + content hash.
/// </summary>
/// <remarks>
/// <b>At-least-once + idempotent (X5).</b> The blob key is <b>deterministic</b> — derived from the row id
/// (itself a non-enumerable uuidv7; <see cref="IBlobStore"/> has no <c>List</c>, EXT-D0e) — so a re-run
/// overwrites the same object instead of orphaning one, and a delivery after the row is already
/// <c>ready</c>/<c>failed</c>/<c>expired</c> is a no-op. The pollable/downloadable <c>export_results</c>
/// row is disposable (X2): short TTL + two-phase reclaim (<see cref="ExportReapHandler"/>).
/// </remarks>
public sealed class ExportBuildHandler(
    AppDbContext db,
    IBlobStore blobStore,
    IClock clock,
    ILogger<ExportBuildHandler> logger) : IJobHandler
{
    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new(JsonSerializerDefaults.Web);

    public string Kind => ExportJobKinds.Build;

    public async Task HandleAsync(JobContext job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<ExportBuildPayload>(job.Payload, PayloadOptions);
        if (payload is null || payload.ExportResultId == Guid.Empty)
            throw new InvalidOperationException($"export.build job {job.Id} has no valid exportResultId.");

        var row = await db.ExportResults.FirstOrDefaultAsync(r => r.Id == payload.ExportResultId, ct);
        if (row is null)
        {
            // Row gone (e.g. GDPR anonymize deleted it, §4.6). Nothing to build.
            logger.LogInformation("export.build: no export_results row for {Id}; treating as done", payload.ExportResultId);
            return;
        }

        if (row.Status != "pending")
            return; // already ready / failed / expired — idempotent no-op

        var built = await TryBuildBytesAsync(row, ct);
        if (built is null)
        {
            // The data subject is gone (GDPR build returned null): terminal, not retryable.
            row.Status = "failed";
            row.Error = "Export subject is unavailable.";
            row.CompletedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogWarning("export.build: subject unavailable for {Id}; marked failed", row.Id);
            return;
        }

        var bytes = built;
        // The object key IS the row id (Guid.ToString()) — matches the E0 blob.hard_delete key scheme so
        // two-phase reclaim (X4) works with the platform handler unchanged; deterministic ⇒ idempotent (X5).
        var storageKey = row.Id.ToString();

        await using (var upload = new MemoryStream(bytes, writable: false))
            await blobStore.PutAsync(storageKey, upload, row.ContentType, ct);

        row.StorageKey = storageKey;
        row.ByteSize = bytes.LongLength;
        row.ContentHash = SHA256.HashData(bytes);
        row.Status = "ready";
        row.CompletedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("export.build: {Kind} artifact ready for {Id} ({Bytes} bytes)", row.Kind, row.Id, bytes.Length);
    }

    /// <summary>Render the artifact bytes, or null when a GDPR subject no longer exists (terminal failure).</summary>
    private async Task<byte[]?> TryBuildBytesAsync(ExportResult row, CancellationToken ct)
    {
        var parameters = JsonSerializer.Deserialize<ExportParams>(row.Params, PayloadOptions) ?? new ExportParams(null, null, null);

        switch (row.Kind)
        {
            case ExportKinds.Csv:
            {
                var (from, to) = ResolveRange(parameters);
                var dialect = CsvDialect.ForLocale(parameters.Locale);
                await using var ms = new MemoryStream();
                await new LedgerCsvExporter(db).WriteAsync(ms, RequireGroup(row), from, to, dialect, ct);
                return ms.ToArray();
            }

            case ExportKinds.Pdf:
            {
                var (from, to) = ResolveRange(parameters);
                var statement = await new LedgerReader(db).ReadAsync(RequireGroup(row), from, to, ct);
                await using var ms = new MemoryStream();
                await new LedgerPdfRenderer().RenderStatementAsync(statement, ms, ct);
                return ms.ToArray();
            }

            case ExportKinds.Gdpr:
            {
                // Reuse the v1 §4.4 redaction posture verbatim (EXT-D4i) — the async sibling of GET /me/export.
                var envelope = await new MeExportBuilder(db, clock).BuildAsync(row.RequestedBy, ct);
                if (envelope is null)
                    return null; // subject anonymized/gone
                var json = JsonSerializer.SerializeToUtf8Bytes(envelope, ArtifactJsonOptions);
                return json;
            }

            default:
                throw new InvalidOperationException($"export.build: unknown kind '{row.Kind}' for {row.Id}.");
        }
    }

    private (DateOnly From, DateOnly To) ResolveRange(ExportParams p) =>
        ExportDates.Resolve(p.From, p.To, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime));

    private static Guid RequireGroup(ExportResult row) =>
        row.GroupId ?? throw new InvalidOperationException($"export.build: {row.Kind} export {row.Id} has no group_id.");

    /// <summary>Job payload (ids only, §4.3): the <c>export_results</c> row to build.</summary>
    private sealed record ExportBuildPayload(
        [property: JsonPropertyName("exportResultId")] Guid ExportResultId);

    /// <summary>The scalar build params stored on <c>export_results.params</c> (no money/PII).</summary>
    private sealed record ExportParams(
        [property: JsonPropertyName("from")] string? From,
        [property: JsonPropertyName("to")] string? To,
        [property: JsonPropertyName("locale")] string? Locale);
}

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Common.Validation;
using Paybitch.Api.Features.Activity;
using Paybitch.Api.Features.Expenses;
using Paybitch.Api.Features.Platform.Blob;
using Paybitch.Api.Features.Platform.Jobs;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Exports;

/// <summary>
/// E4 — Exports &amp; reporting (§4). One module maps all six surfaces (EXT-D4a–j):
/// <list type="bullet">
///   <item><c>GET …/expenses/export.csv</c> — synchronous, streamed Czech/English CSV (no job, no blob).</item>
///   <item><c>GET …/stats</c> — on-demand per-currency spend aggregates (X2, D5).</item>
///   <item><c>POST …/exports</c> / <c>POST /me/export</c> — enqueue an async CSV/PDF / GDPR build (202).</item>
///   <item><c>GET /exports/{id}</c> / <c>…/download</c> — requester-scoped poll + authenticated stream (D6).</item>
/// </list>
/// Group routes run the D6 membership check first; <c>/exports/{id}[/download]</c> are requester-scoped
/// (<c>requested_by = @me</c>) → another user's artifact is a uniform <c>404</c> (EXT-D4f, no existence leak).
/// </summary>
public sealed class ExportsModule : IEndpointModule
{
    private const string V1 = "/v1";

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/groups/{groupId:guid}/expenses/export.csv", ExportCsvAsync)
            .RequireGroupMembership()
            .WithName("ExportGroupExpensesCsv")
            .WithSummary("Synchronous streamed CSV ledger export (Czech/English dialect).")
            .WithTags("Exports");

        app.MapGet("/groups/{groupId:guid}/stats", StatsAsync)
            .RequireGroupMembership()
            .WithName("GetGroupStats")
            .WithSummary("On-demand per-currency spend statistics for in-app charts.")
            .WithTags("Exports");

        app.MapPost("/groups/{groupId:guid}/exports", CreateGroupExportAsync)
            .RequireGroupMembership()
            .WithValidation()
            .WithName("EnqueueGroupExport")
            .WithSummary("Enqueue an async CSV/PDF export build → 202 with a pollable id.")
            .WithTags("Exports");

        app.MapPost("/me/export", CreateGdprExportAsync)
            .WithName("EnqueueGdprExport")
            .WithSummary("Enqueue an async GDPR data export (Art. 15/20) → 202 with a pollable id.")
            .WithTags("Exports");

        app.MapGet("/exports/{id:guid}", GetExportAsync)
            .WithName("GetExportResult")
            .WithSummary("Poll an export's status (requester-scoped).")
            .WithTags("Exports");

        app.MapGet("/exports/{id:guid}/download", DownloadExportAsync)
            .WithName("DownloadExportResult")
            .WithSummary("Download a ready export artifact (authenticated stream, requester-scoped).")
            .WithTags("Exports");
    }

    // --- GET …/expenses/export.csv (EXT-D4a/c/j) ---
    private static async Task<IResult> ExportCsvAsync(
        Guid groupId, string? from, string? to, string? locale,
        AppDbContext db, IActivityWriter activity, IClock clock, HttpContext http, CancellationToken ct)
    {
        if (from is not null && !ExpenseWire.TryParseDate(from, out _))
            return Problems.Validation(ProblemCodes.DateOutOfRange, "from", "from must be an ISO date (yyyy-MM-dd).");
        if (to is not null && !ExpenseWire.TryParseDate(to, out _))
            return Problems.Validation(ProblemCodes.DateOutOfRange, "to", "to must be an ISO date (yyyy-MM-dd).");

        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var (rangeFrom, rangeTo) = ExportDates.Resolve(from, to, today);
        var dialect = CsvDialect.ForLocale(locale);

        // Audit the export request (EXT-D4f) BEFORE streaming begins — ids-only metadata, no amounts (§4.3).
        var membership = http.GetMembership();
        activity.Write(groupId, membership.UserId, ExportActivity.GroupExported,
            ActivityTargetTypes.Group, groupId, new { kind = ExportKinds.Csv, mode = "sync" });
        await db.SaveChangesAsync(ct);

        var exporter = new LedgerCsvExporter(db);
        var fileName = $"paybitch-expenses-{groupId}.csv";
        return Results.Stream(
            stream => exporter.WriteAsync(stream, groupId, rangeFrom, rangeTo, dialect, ct),
            contentType: "text/csv; charset=utf-8",
            fileDownloadName: fileName);
    }

    // --- GET …/stats (EXT-D4g/h) ---
    private static async Task<IResult> StatsAsync(
        Guid groupId, string? granularity, string? from, string? to, string? tz, string? convert,
        AppDbContext db, IClock clock, CancellationToken ct)
    {
        var gran = string.IsNullOrEmpty(granularity) ? "month" : granularity;
        if (!StatsService.AllowedGranularities.Contains(gran))
            return Problems.Validation(ProblemCodes.ValidationFailed, "granularity", "granularity must be one of: day, week, month.");

        if (from is not null && !ExpenseWire.TryParseDate(from, out _))
            return Problems.Validation(ProblemCodes.DateOutOfRange, "from", "from must be an ISO date (yyyy-MM-dd).");
        if (to is not null && !ExpenseWire.TryParseDate(to, out _))
            return Problems.Validation(ProblemCodes.DateOutOfRange, "to", "to must be an ISO date (yyyy-MM-dd).");

        var zone = string.IsNullOrEmpty(tz) ? "Europe/Prague" : tz;
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(zone, out var tzInfo))
            return Problems.Validation(ProblemCodes.ValidationFailed, "tz", "tz must be a valid IANA time zone id.");

        // tz resolves ONLY the open-ended upper bound ("up to now"); explicit dates bucket verbatim (§1.3).
        var todayInTz = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, tzInfo).DateTime);
        var (rangeFrom, rangeTo) = ExportDates.Resolve(from, to, todayInTz);

        var stats = await new StatsService(db).ComputeAsync(groupId, gran, rangeFrom, rangeTo, zone, ct);
        // `convert` is accepted for forward-compat; the X3 `display` block is emitted only once E2 (Live FX)
        // wires a converter (EXT-D4h). Until then the response carries native per-currency buckets only.
        _ = convert;
        return Results.Ok(stats);
    }

    // --- POST …/exports (EXT-D4a/d) ---
    private static async Task<IResult> CreateGroupExportAsync(
        Guid groupId, CreateExportRequest request,
        AppDbContext db, IJobQueue jobQueue, IActivityWriter activity, IClock clock, HttpContext http, CancellationToken ct)
    {
        var membership = http.GetMembership();
        var kind = request.Kind!; // validator guarantees ∈ {csv, pdf}

        var existing = await FindInflightAsync(db, membership.UserId, groupId, kind, request.From, request.To, request.Locale, ct);
        if (existing is not null)
            return Accepted(existing);

        var row = NewPendingRow(membership.UserId, groupId, kind, SerializeParams(groupId, request.From, request.To, request.Locale), clock);
        db.ExportResults.Add(row);
        activity.Write(groupId, membership.UserId, ExportActivity.GroupExported,
            ActivityTargetTypes.Group, groupId, new { kind, mode = "async" });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsInflightCollision(ex))
        {
            // uq_export_results_inflight lost the race to a concurrent identical request — collapse to it (X5).
            db.ChangeTracker.Clear();
            var raced = await FindInflightAsync(db, membership.UserId, groupId, kind, request.From, request.To, request.Locale, ct);
            if (raced is not null)
                return Accepted(raced);
            throw;
        }

        await EnqueueBuildAndReapAsync(jobQueue, row, ct);
        return Accepted(row);
    }

    // --- POST /me/export (EXT-D4i) ---
    private static async Task<IResult> CreateGdprExportAsync(
        AppDbContext db, IJobQueue jobQueue, IClock clock, ICurrentUser currentUser,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var userId = currentUser.UserId;

        // uq_export_results_inflight keys on (requested_by, kind, md5(params)) — for GDPR that is
        // (user, 'gdpr', md5('{}')), i.e. one in-flight GDPR export per user. Pre-check collapses the
        // mashed button; the try/catch collapses a concurrent race to the same row.
        var existing = await FindInflightAsync(db, userId, groupId: null, ExportKinds.Gdpr, null, null, null, ct);
        if (existing is not null)
            return Accepted(existing);

        var row = NewPendingRow(userId, groupId: null, ExportKinds.Gdpr, "{}", clock);
        db.ExportResults.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsInflightCollision(ex))
        {
            db.ChangeTracker.Clear();
            var raced = await FindInflightAsync(db, userId, groupId: null, ExportKinds.Gdpr, null, null, null, ct);
            if (raced is not null)
                return Accepted(raced);
            throw;
        }

        // A GDPR self-export is recorded in the Serilog audit stream, not the group activity feed (§4.6).
        loggerFactory.CreateLogger("Paybitch.Exports.Gdpr")
            .LogInformation("GDPR export {ExportId} requested by user {UserId}", row.Id, userId);

        await EnqueueBuildAndReapAsync(jobQueue, row, ct);
        return Accepted(row);
    }

    // --- GET /exports/{id} (poll, requester-scoped) ---
    private static async Task<IResult> GetExportAsync(
        Guid id, AppDbContext db, IClock clock, ICurrentUser currentUser, CancellationToken ct)
    {
        var row = await db.ExportResults.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.RequestedBy == currentUser.UserId, ct);
        if (row is null)
            return Problems.NotFound(); // another user's id → 404 (D6 posture, EXT-D4f)

        var expired = row.ExpiresAt <= clock.UtcNow || row.Status == "expired";
        var ready = row.Status == "ready" && !expired;
        var status = expired ? "expired" : row.Status;

        return Results.Ok(new ExportStatusResponse(
            row.Id.ToString(),
            row.Kind,
            status,
            ready ? row.ContentType : null,
            ready ? row.ByteSize : null,
            row.ExpiresAt,
            ready ? $"{V1}/exports/{row.Id}/download" : null));
    }

    // --- GET /exports/{id}/download (authenticated stream, requester-scoped, EXT-D4e) ---
    private static async Task<IResult> DownloadExportAsync(
        Guid id, AppDbContext db, IBlobStore blobStore, IClock clock, ICurrentUser currentUser, CancellationToken ct)
    {
        var row = await db.ExportResults.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.RequestedBy == currentUser.UserId, ct);
        if (row is null)
            return Problems.NotFound();

        if (row.ExpiresAt <= clock.UtcNow || row.Status == "expired")
            return Problems.Gone(ExportProblemCodes.ExportExpired); // §4.8: download after expiry → 410

        if (row.Status != "ready" || string.IsNullOrEmpty(row.StorageKey))
            return Problems.NotFound(); // not built yet — uniform 404

        // Default = authenticated stream, so D6 stays in force on every byte (EXT-D4e). A short-TTL presigned
        // GET (IBlobStore.TryGetPresignedGetUrl) is the large-artifact fallback only; the dev store can't
        // presign (returns null) so this is the only path here.
        var stream = await blobStore.GetAsync(row.StorageKey, ct);
        if (stream is null)
            return Problems.NotFound(); // blob already reclaimed — uniform 404

        return Results.Stream(stream, contentType: row.ContentType, fileDownloadName: DownloadFileName(row));
    }

    // --- helpers ---

    private static ExportResult NewPendingRow(Guid userId, Guid? groupId, string kind, string paramsJson, IClock clock)
    {
        var now = clock.UtcNow;
        return new ExportResult
        {
            RequestedBy = userId,
            GroupId = groupId,
            Kind = kind,
            ContentType = ExportContentTypes.ForKind(kind),
            Params = paramsJson,
            Status = "pending",
            RequestedAt = now,
            ExpiresAt = now + ExportOperational.ArtifactTtl,
        };
    }

    private static async Task EnqueueBuildAndReapAsync(IJobQueue jobQueue, ExportResult row, CancellationToken ct)
    {
        await jobQueue.EnqueueAsync(
            ExportJobKinds.Build, new { exportResultId = row.Id },
            dedupeKey: $"{ExportJobKinds.Build}:{row.Id}", runAt: null, ct: ct);

        // Delayed TTL reaper (X4, §4.6) fires at expires_at → mark expired + two-phase blob reclaim.
        await jobQueue.EnqueueAsync(
            ExportJobKinds.Reap, new { exportResultId = row.Id },
            dedupeKey: $"{ExportJobKinds.Reap}:{row.Id}", runAt: row.ExpiresAt, ct: ct);
    }

    private static IResult Accepted(ExportResult row) =>
        Results.Accepted(
            $"{V1}/exports/{row.Id}",
            new EnqueuedExportResponse(row.Id.ToString(), row.Kind, row.Status));

    /// <summary>
    /// Serialize the build params deterministically (stable key order ⇒ stable md5 for the dedupe index).
    /// <paramref name="groupId"/> is folded in because <c>uq_export_results_inflight</c> keys on
    /// <c>(requested_by, kind, md5(params::text))</c> only (NOT group_id) — without it, two different
    /// groups' same-kind/same-date exports would collide. Ids/scalars only, no money/PII (§4.3).
    /// </summary>
    private static string SerializeParams(Guid? groupId, string? from, string? to, string? locale) =>
        JsonSerializer.Serialize(new { groupId = groupId?.ToString(), from, to, locale });

    /// <summary>
    /// Find the caller's one live (<c>pending</c>) export for this exact request, matching on the LOGICAL
    /// params (deserialized) so jsonb text normalization can't cause a miss. Backs both the pre-insert
    /// collapse and the post-collision race resolution.
    /// </summary>
    private static async Task<ExportResult?> FindInflightAsync(
        AppDbContext db, Guid userId, Guid? groupId, string kind,
        string? from, string? to, string? locale, CancellationToken ct)
    {
        var candidates = await db.ExportResults.AsNoTracking()
            .Where(r => r.RequestedBy == userId && r.GroupId == groupId && r.Kind == kind && r.Status == "pending")
            .ToListAsync(ct);

        return candidates.FirstOrDefault(r => ParamsMatch(r.Params, from, to, locale));
    }

    private static bool ParamsMatch(string storedParams, string? from, string? to, string? locale)
    {
        try
        {
            using var doc = JsonDocument.Parse(storedParams);
            var root = doc.RootElement;
            return ReadString(root, "from") == from
                   && ReadString(root, "to") == to
                   && ReadString(root, "locale") == locale;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    /// <summary>True when the failure is the <c>uq_export_results_inflight</c> partial-unique violation.</summary>
    private static bool IsInflightCollision(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && pg.ConstraintName == "uq_export_results_inflight";

    private static string DownloadFileName(ExportResult row)
    {
        var extension = row.ContentType switch
        {
            ExportContentTypes.Csv => "csv",
            ExportContentTypes.Pdf => "pdf",
            ExportContentTypes.Json => "json",
            _ => "bin",
        };
        return $"paybitch-{row.Kind}-{row.Id}.{extension}";
    }
}

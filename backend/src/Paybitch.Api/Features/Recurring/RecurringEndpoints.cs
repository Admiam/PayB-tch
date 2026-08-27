using System.Globalization;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Common.Pagination;
using Paybitch.Api.Features.Expenses;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Recurring;

/// <summary>
/// Handlers for the recurring-rule aggregate (§3.4). Every route is behind the D6 membership filter (404 for
/// non-members). Mutations reject an archived group (409 <c>group_archived</c>), require <c>If-Match</c>
/// (money-aggregate concurrency, D8), append a <c>recurring_rule</c> change_log row in the same transaction
/// (§3.5.2), and bump <c>version</c> exactly once. Money is string minor units (D1); no FX, ever (EXT-D3k).
/// </summary>
public static class RecurringEndpoints
{
    private const int DefaultLimit = 25;
    private const int MaxLimit = 100;

    // ---------- CREATE (§3.4) ----------

    public static async Task<IResult> CreateAsync(
        CreateRecurringRuleRequest request,
        HttpContext http,
        AppDbContext db,
        IChangeLogWriter changeLog,
        IClock clock,
        CancellationToken ct)
    {
        var m = http.GetMembership();
        var groupId = m.GroupId;

        if (await RejectIfArchivedAsync(db, groupId, ct) is { } archived)
            return archived;

        if (RecurringSemantics.TryParseRecurrence(request.Recurrence, out var rec) is { } recError)
            return recError;

        var activeMembers = await ActiveMemberIdsAsync(db, groupId, ct);
        var amountMinor = ExpenseWire.ParseMinor(request.Amount!);

        if (RecurringSemantics.ValidateSplit(request.Split, activeMembers, amountMinor) is { } splitError)
            return splitError;

        var paidBy = Guid.Parse(request.PaidBy!);
        if (!activeMembers.Contains(paidBy))
            return Problems.Validation(ProblemCodes.SplitMemberInvalid, "paidBy", "paidBy is not a member of this group.");

        var categoryId = ParseOptionalGuid(request.CategoryId);
        if (await ValidateCategoryAsync(db, groupId, categoryId, ct) is { } categoryError)
            return categoryError;

        var ruleId = Guid.CreateVersion7();
        var rule = BuildRule(ruleId, groupId, request, rec, m.UserId);
        var splitRows = RecurringMapping.BuildSplitRows(ruleId, request.Split!);
        var requestCanonical = RecurringMapping.Canonicalize(rule, splitRows);

        // D9 idempotency: a replayed (group, clientId) is diffed, never re-applied.
        if (await FindByClientIdAsync(db, groupId, request.ClientId!, ct) is { } existing)
            return Replay(http, existing.Rule, existing.Rows, requestCanonical);

        var liveCount = await db.RecurringRules.CountAsync(
            r => r.GroupId == groupId && r.DeletedAt == null && r.Status != RuleStatus.Ended, ct);
        if (liveCount >= RecurringLimits.MaxActiveRulesPerGroup)
            return Problems.Conflict(ProblemCodes.LimitExceeded);

        ApplyInitialSchedule(rule, clock);

        db.RecurringRules.Add(rule);
        foreach (var row in splitRows)
            db.RecurringRuleSplits.Add(row);
        changeLog.Append(groupId, ChangeLogEntityTypes.RecurringRule, ruleId, isDelete: false);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            if (await FindByClientIdAsync(db, groupId, request.ClientId!, ct) is not { } raced)
                throw;
            return Replay(http, raced.Rule, raced.Rows, requestCanonical);
        }

        SetETag(http, rule.Version);
        return Results.Json(RecurringMapping.ToResponse(rule, splitRows), statusCode: StatusCodes.Status201Created);
    }

    // ---------- LIST (active + paused, cursor, §3.4) ----------

    public static async Task<IResult> ListAsync(
        HttpContext http,
        AppDbContext db,
        string? cursor,
        int? limit,
        CancellationToken ct)
    {
        var m = http.GetMembership();
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        Guid? afterId = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!Cursor.TryDecode(cursor, out var payload) || !Guid.TryParse(payload, out var id))
                return Problems.Validation(ProblemCodes.ValidationFailed, "cursor", "Malformed cursor.");
            afterId = id;
        }

        // Keyset over id DESC (v7 ids are time-ordered). Raw SQL so the uuid comparison runs natively — C#'s
        // Guid has no < operator for EF to translate.
        var hasCursor = afterId is not null;
        var sql = "SELECT id FROM recurring_rules WHERE group_id = @groupId AND deleted_at IS NULL AND status <> 'ended'"
                  + (hasCursor ? " AND id < @afterId" : string.Empty)
                  + " ORDER BY id DESC LIMIT @lim";
        object parameters = hasCursor
            ? new { groupId = m.GroupId, afterId = afterId!.Value, lim = take + 1 }
            : new { groupId = m.GroupId, lim = take + 1 };

        var conn = db.Database.GetDbConnection();
        var ids = (await conn.QueryAsync<Guid>(new CommandDefinition(sql, parameters, cancellationToken: ct))).ToList();
        if (ids.Count == 0)
            return Results.Ok(new Page<RecurringRuleResponse>([], new PageInfo(null, false, take)));

        var pageIds = (ids.Count > take ? ids.Take(take) : ids).ToList();
        var rules = await db.RecurringRules.AsNoTracking().Where(r => pageIds.Contains(r.Id)).ToListAsync(ct);
        var rows = await db.RecurringRuleSplits.AsNoTracking().Where(s => pageIds.Contains(s.RuleId)).ToListAsync(ct);

        var byId = rules.ToDictionary(r => r.Id);
        var rowsByRule = rows.GroupBy(s => s.RuleId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<RecurringRuleSplit>)g.ToList());

        var data = pageIds
            .Where(byId.ContainsKey)
            .Select(rid => RecurringMapping.ToResponse(
                byId[rid], rowsByRule.TryGetValue(rid, out var s) ? s : []))
            .ToList();

        var hasMore = ids.Count > take;
        var nextCursor = hasMore ? Cursor.Encode(pageIds[^1].ToString()) : null;
        return Results.Ok(new Page<RecurringRuleResponse>(data, new PageInfo(nextCursor, hasMore, take)));
    }

    // ---------- GET detail (§3.4) ----------

    public static async Task<IResult> GetAsync(string ruleId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var m = http.GetMembership();
        if (!Guid.TryParse(ruleId, out var rid))
            return Problems.NotFound();

        var rule = await db.RecurringRules.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == rid && r.GroupId == m.GroupId && r.DeletedAt == null, ct);
        if (rule is null)
            return Problems.NotFound();

        var rows = await SplitRowsAsync(db, rid, ct);
        SetETag(http, rule.Version);
        return Results.Ok(RecurringMapping.ToResponse(rule, rows));
    }

    // ---------- REPLACE (full-aggregate PUT, future-only, §3.4/EXT-D3h) ----------

    public static async Task<IResult> ReplaceAsync(
        string ruleId,
        ReplaceRecurringRuleRequest request,
        HttpContext http,
        AppDbContext db,
        IChangeLogWriter changeLog,
        IClock clock,
        CancellationToken ct)
    {
        var m = http.GetMembership();
        var groupId = m.GroupId;
        if (!Guid.TryParse(ruleId, out var rid))
            return Problems.NotFound();

        if (await RejectIfArchivedAsync(db, groupId, ct) is { } archived)
            return archived;

        if (RecurringSemantics.TryParseRecurrence(request.Recurrence, out var rec) is { } recError)
            return recError;

        var activeMembers = await ActiveMemberIdsAsync(db, groupId, ct);
        var amountMinor = ExpenseWire.ParseMinor(request.Amount!);
        if (RecurringSemantics.ValidateSplit(request.Split, activeMembers, amountMinor) is { } splitError)
            return splitError;

        var paidBy = Guid.Parse(request.PaidBy!);
        if (!activeMembers.Contains(paidBy))
            return Problems.Validation(ProblemCodes.SplitMemberInvalid, "paidBy", "paidBy is not a member of this group.");

        var categoryId = ParseOptionalGuid(request.CategoryId);
        if (await ValidateCategoryAsync(db, groupId, categoryId, ct) is { } categoryError)
            return categoryError;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var rule = await LockRuleAsync(db, groupId, rid, ct);
        if (rule is null || rule.DeletedAt is not null)
            return Problems.NotFound();

        var ifMatch = ReadIfMatch(http);
        if (!ifMatch.Present)
            return Problems.PreconditionRequired();
        if (ifMatch.Version != rule.Version)
            return Problems.VersionConflict(RecurringMapping.ToResponse(rule, await SplitRowsAsync(db, rid, ct)));

        if (CheckImmutableFields(request, rule) is { } immutableError)
            return immutableError;

        var keepPaused = rule.Status == RuleStatus.Paused;

        rule.Title = request.Title!;
        rule.AmountMinor = amountMinor;
        rule.Currency = request.Currency!;
        rule.PaidBy = paidBy;
        rule.SplitType = request.Split!.Type!;
        rule.CategoryId = categoryId;
        rule.IconSymbol = request.IconSymbol;
        rule.Notes = request.Notes;
        rule.Freq = rec.Freq;
        rule.Interval = rec.Interval;
        rule.ByMonthDay = rec.ByMonthDay;
        rule.ByWeekday = rec.ByWeekday;
        rule.Timezone = rec.Timezone;
        rule.StartsOn = rec.StartsOn;
        rule.EndsOn = rec.EndsOn;
        rule.RemainingCount = rec.RemainingCount;

        // Replace the split-template rows (delete-then-reinsert), future-only (EXT-D3h).
        await db.RecurringRuleSplits.Where(s => s.RuleId == rid).ExecuteDeleteAsync(ct);
        var newRows = RecurringMapping.BuildSplitRows(rid, request.Split!);
        foreach (var row in newRows)
            db.RecurringRuleSplits.Add(row);

        // Recompute next_run_at from the NEW recurrence, future-only (never re-fire a past occurrence).
        RecurrenceEngine.TryResolveTimeZone(rule.Timezone, out var tz);
        var jitter = RecurrenceEngine.JitterMinutes(rule.Id);
        var floor = Greatest(rec.StartsOn, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime));
        var firstRun = RecurrenceEngine.FirstRunOnOrAfter(rule, tz, jitter, floor);
        if (firstRun is null)
        {
            rule.Status = RuleStatus.Ended;
            rule.NextRunAt = null;
            rule.PauseReason = null;
        }
        else
        {
            rule.Status = keepPaused ? RuleStatus.Paused : RuleStatus.Active;
            rule.NextRunAt = firstRun;
            if (!keepPaused)
                rule.PauseReason = null;
        }

        rule.Version += 1;
        changeLog.Append(groupId, ChangeLogEntityTypes.RecurringRule, rid, isDelete: false);

        await db.SaveChangesAsync(ct);
        var savedRows = await SplitRowsAsync(db, rid, ct);
        await tx.CommitAsync(ct);

        SetETag(http, rule.Version);
        return Results.Ok(RecurringMapping.ToResponse(rule, savedRows));
    }

    // ---------- DELETE (soft, §3.4) ----------

    public static async Task<IResult> DeleteAsync(
        string ruleId, HttpContext http, AppDbContext db, IChangeLogWriter changeLog, IClock clock, CancellationToken ct)
    {
        var m = http.GetMembership();
        var groupId = m.GroupId;
        if (!Guid.TryParse(ruleId, out var rid))
            return Problems.NotFound();

        if (await RejectIfArchivedAsync(db, groupId, ct) is { } archived)
            return archived;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var rule = await LockRuleAsync(db, groupId, rid, ct);
        if (rule is null || rule.DeletedAt is not null)
            return Problems.NotFound(); // replayed delete finds the tombstone → 404 (outbox treats as success)

        var ifMatch = ReadIfMatch(http);
        if (!ifMatch.Present)
            return Problems.PreconditionRequired();
        if (ifMatch.Version != rule.Version)
            return Problems.VersionConflict(RecurringMapping.ToResponse(rule, await SplitRowsAsync(db, rid, ct)));

        rule.DeletedAt = clock.UtcNow;
        rule.Version += 1;
        changeLog.Append(groupId, ChangeLogEntityTypes.RecurringRule, rid, isDelete: true);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        SetETag(http, rule.Version);
        return Results.NoContent();
    }

    // ---------- PAUSE (active → paused, idempotent, §3.4) ----------

    public static Task<IResult> PauseAsync(
        string ruleId, HttpContext http, AppDbContext db, IChangeLogWriter changeLog, IClock clock, CancellationToken ct) =>
        TransitionAsync(ruleId, http, db, changeLog, clock, ct, resume: false);

    // ---------- RESUME (paused → active, roll forward, §3.4) ----------

    public static Task<IResult> ResumeAsync(
        string ruleId, HttpContext http, AppDbContext db, IChangeLogWriter changeLog, IClock clock, CancellationToken ct) =>
        TransitionAsync(ruleId, http, db, changeLog, clock, ct, resume: true);

    private static async Task<IResult> TransitionAsync(
        string ruleId, HttpContext http, AppDbContext db, IChangeLogWriter changeLog, IClock clock,
        CancellationToken ct, bool resume)
    {
        var m = http.GetMembership();
        var groupId = m.GroupId;
        if (!Guid.TryParse(ruleId, out var rid))
            return Problems.NotFound();

        if (await RejectIfArchivedAsync(db, groupId, ct) is { } archived)
            return archived;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var rule = await LockRuleAsync(db, groupId, rid, ct);
        if (rule is null || rule.DeletedAt is not null)
            return Problems.NotFound();

        var ifMatch = ReadIfMatch(http);
        if (!ifMatch.Present)
            return Problems.PreconditionRequired();
        if (ifMatch.Version != rule.Version)
            return Problems.VersionConflict(RecurringMapping.ToResponse(rule, await SplitRowsAsync(db, rid, ct)));

        if (rule.Status == RuleStatus.Ended)
            return Problems.Conflict(RecurringProblemCodes.RuleNotResumable);

        var targetStatus = resume ? RuleStatus.Active : RuleStatus.Paused;
        if (rule.Status == targetStatus)
        {
            // Idempotent no-op: same state, no version bump, no change_log row.
            await tx.CommitAsync(ct);
            SetETag(http, rule.Version);
            return Results.Ok(RecurringMapping.ToResponse(rule, await SplitRowsAsync(db, rid, ct)));
        }

        if (resume)
        {
            RecurrenceEngine.TryResolveTimeZone(rule.Timezone, out var tz);
            var jitter = RecurrenceEngine.JitterMinutes(rule.Id);
            var firstRun = RecurrenceEngine.FirstRunStrictlyAfter(rule, tz, jitter, clock.UtcNow); // skip the paused window
            if (firstRun is null)
            {
                rule.Status = RuleStatus.Ended;
                rule.NextRunAt = null;
            }
            else
            {
                rule.Status = RuleStatus.Active;
                rule.NextRunAt = firstRun;
            }
            rule.PauseReason = null;
        }
        else
        {
            rule.Status = RuleStatus.Paused;
            rule.PauseReason = PauseReasons.User; // next_run_at retained (non-null); resume rolls it forward
        }

        rule.Version += 1;
        changeLog.Append(groupId, ChangeLogEntityTypes.RecurringRule, rid, isDelete: false);

        await db.SaveChangesAsync(ct);
        var savedRows = await SplitRowsAsync(db, rid, ct);
        await tx.CommitAsync(ct);

        SetETag(http, rule.Version);
        return Results.Ok(RecurringMapping.ToResponse(rule, savedRows));
    }

    // ---------- OCCURRENCES preview (computed, X2, §3.4) ----------

    public static async Task<IResult> OccurrencesAsync(
        string ruleId, HttpContext http, AppDbContext db, string? until, IClock clock, CancellationToken ct)
    {
        var m = http.GetMembership();
        if (!Guid.TryParse(ruleId, out var rid))
            return Problems.NotFound();

        var rule = await db.RecurringRules.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == rid && r.GroupId == m.GroupId && r.DeletedAt == null, ct);
        if (rule is null)
            return Problems.NotFound();

        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var horizon = today.AddMonths(RecurringLimits.PreviewHorizonMonths);
        var until_ = horizon;
        if (until is not null)
        {
            if (!ExpenseWire.TryParseDate(until, out var parsed))
                return Problems.Validation(ProblemCodes.DateOutOfRange, "until", "until must be a valid ISO date.");
            until_ = parsed < horizon ? parsed : horizon; // clamp to the 24-month horizon
        }

        var results = new List<OccurrenceItem>();
        if (rule.NextRunAt is { } nextRun && rule.Status != RuleStatus.Ended
            && RecurrenceEngine.TryResolveTimeZone(rule.Timezone, out var tz))
        {
            var jitter = RecurrenceEngine.JitterMinutes(rule.Id);
            var cap = Math.Min(RecurringLimits.PreviewMaxRows, rule.RemainingCount ?? int.MaxValue);
            foreach (var occ in RecurrenceEngine.EnumerateOccurrences(rule, tz, jitter))
            {
                if (occ.RunsAt < nextRun)
                    continue; // before the current position
                if (occ.Date > until_)
                    break;
                results.Add(new OccurrenceItem(ExpenseWire.Date(occ.Date), ExpenseWire.Timestamp(occ.RunsAt)));
                if (results.Count >= cap)
                    break;
            }
        }

        return Results.Ok(new OccurrencesResponse(rule.Id.ToString(), rule.Timezone, results));
    }

    // ---------- shared helpers ----------

    private static void ApplyInitialSchedule(RecurringRule rule, IClock clock)
    {
        RecurrenceEngine.TryResolveTimeZone(rule.Timezone, out var tz);
        var jitter = RecurrenceEngine.JitterMinutes(rule.Id);
        var floor = Greatest(rule.StartsOn, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime));
        var firstRun = RecurrenceEngine.FirstRunOnOrAfter(rule, tz, jitter, floor);
        if (firstRun is null)
        {
            rule.Status = RuleStatus.Ended;   // no occurrence ⇒ born ended (satisfies the ended⇔null CHECK)
            rule.NextRunAt = null;
        }
        else
        {
            rule.Status = RuleStatus.Active;
            rule.NextRunAt = firstRun;
        }
    }

    private static RecurringRule BuildRule(
        Guid ruleId, Guid groupId, IRecurringRuleBody body, ParsedRecurrence rec, Guid createdBy) =>
        new()
        {
            Id = ruleId,
            GroupId = groupId,
            ClientId = (body as CreateRecurringRuleRequest)?.ClientId,
            Title = body.Title!,
            AmountMinor = ExpenseWire.ParseMinor(body.Amount!),
            Currency = body.Currency!,
            PaidBy = Guid.Parse(body.PaidBy!),
            SplitType = body.Split!.Type!,
            CategoryId = ParseOptionalGuid(body.CategoryId),
            IconSymbol = body.IconSymbol,
            Notes = body.Notes,
            Freq = rec.Freq,
            Interval = rec.Interval,
            ByMonthDay = rec.ByMonthDay,
            ByWeekday = rec.ByWeekday,
            Timezone = rec.Timezone,
            StartsOn = rec.StartsOn,
            EndsOn = rec.EndsOn,
            RemainingCount = rec.RemainingCount,
            Version = 1,
            CreatedBy = createdBy,
        };

    private static IResult Replay(
        HttpContext http, RecurringRule existing, IReadOnlyList<RecurringRuleSplit> rows, string requestCanonical)
    {
        var existingCanonical = RecurringMapping.Canonicalize(existing, rows);
        if (!string.Equals(existingCanonical, requestCanonical, StringComparison.Ordinal))
            return Problems.ClientIdConflict(existing.Id.ToString());

        SetETag(http, existing.Version);
        return Results.Ok(RecurringMapping.ToResponse(existing, rows));
    }

    private static async Task<IResult?> RejectIfArchivedAsync(AppDbContext db, Guid groupId, CancellationToken ct)
    {
        var archivedAt = await db.Groups.AsNoTracking()
            .Where(g => g.Id == groupId).Select(g => g.ArchivedAt).FirstOrDefaultAsync(ct);
        return archivedAt is not null
            ? Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.GroupArchived,
                detail: "Unarchive the group to make changes.")
            : null;
    }

    private static async Task<IResult?> ValidateCategoryAsync(AppDbContext db, Guid groupId, Guid? categoryId, CancellationToken ct)
    {
        if (categoryId is null)
            return null;

        var category = await db.Categories.AsNoTracking()
            .Where(c => c.Id == categoryId)
            .Select(c => new { c.GroupId, c.DeletedAt })
            .FirstOrDefaultAsync(ct);

        if (category is null || (category.GroupId is not null && category.GroupId != groupId))
            return Problems.Validation(ProblemCodes.ValidationFailed, "categoryId", "Unknown category.");
        if (category.DeletedAt is not null)
            return Problems.Validation(ProblemCodes.CategoryDeleted, "categoryId", "Category has been deleted.");
        return null;
    }

    private static IResult? CheckImmutableFields(ReplaceRecurringRuleRequest request, RecurringRule rule)
    {
        var offenders = new List<ProblemError>();
        if (request.Id is not null && !GuidEquals(request.Id, rule.Id))
            offenders.Add(new ProblemError("id", "id cannot be changed."));
        if (request.GroupId is not null && !GuidEquals(request.GroupId, rule.GroupId))
            offenders.Add(new ProblemError("groupId", "groupId cannot be changed."));
        if (request.ClientId is not null && !string.Equals(request.ClientId, rule.ClientId, StringComparison.Ordinal))
            offenders.Add(new ProblemError("clientId", "clientId cannot be changed."));
        if (request.Status is not null && !string.Equals(request.Status, rule.Status, StringComparison.Ordinal))
            offenders.Add(new ProblemError("status", "status is server-owned (use pause/resume)."));
        if (request.CreatedBy is not null && !NullableGuidEquals(request.CreatedBy, rule.CreatedBy))
            offenders.Add(new ProblemError("createdBy", "createdBy cannot be changed."));
        if (request.CreatedAt is not null && !TimestampEquals(request.CreatedAt, rule.CreatedAt))
            offenders.Add(new ProblemError("createdAt", "createdAt cannot be changed."));

        return offenders.Count > 0 ? Problems.Validation(ProblemCodes.ImmutableField, offenders) : null;
    }

    private static async Task<IReadOnlySet<Guid>> ActiveMemberIdsAsync(AppDbContext db, Guid groupId, CancellationToken ct) =>
        (await db.GroupMembers.AsNoTracking()
            .Where(x => x.GroupId == groupId && x.DeletedAt == null)
            .Select(x => x.Id).ToListAsync(ct)).ToHashSet();

    private static async Task<(RecurringRule Rule, IReadOnlyList<RecurringRuleSplit> Rows)?> FindByClientIdAsync(
        AppDbContext db, Guid groupId, string clientId, CancellationToken ct)
    {
        var rule = await db.RecurringRules.AsNoTracking()
            .FirstOrDefaultAsync(r => r.GroupId == groupId && r.ClientId == clientId, ct);
        if (rule is null)
            return null;
        return (rule, await SplitRowsAsync(db, rule.Id, ct));
    }

    private static async Task<RecurringRule?> LockRuleAsync(AppDbContext db, Guid groupId, Guid ruleId, CancellationToken ct)
    {
        // FOR UPDATE serializes concurrent PUT/DELETE/pause/resume AND the worker's advance (D8); held to COMMIT.
        var rows = await db.RecurringRules
            .FromSql($"SELECT * FROM recurring_rules WHERE id = {ruleId} AND group_id = {groupId} FOR UPDATE")
            .ToListAsync(ct);
        return rows.FirstOrDefault();
    }

    private static async Task<IReadOnlyList<RecurringRuleSplit>> SplitRowsAsync(AppDbContext db, Guid ruleId, CancellationToken ct) =>
        await db.RecurringRuleSplits.AsNoTracking().Where(s => s.RuleId == ruleId).ToListAsync(ct);

    private static Guid? ParseOptionalGuid(string? raw) => string.IsNullOrEmpty(raw) ? null : Guid.Parse(raw);

    private static DateOnly Greatest(DateOnly a, DateOnly b) => a >= b ? a : b;

    private static (bool Present, int Version) ReadIfMatch(HttpContext http)
    {
        var raw = http.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return (false, 0);
        var value = raw.Trim();
        if (value.StartsWith("W/", StringComparison.Ordinal))
            value = value[2..];
        value = value.Trim().Trim('"');
        return (true, int.TryParse(value, out var version) ? version : -1); // garbage → -1 never matches → 412
    }

    private static void SetETag(HttpContext http, int version) => http.Response.Headers.ETag = $"\"{version}\"";

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    private static bool GuidEquals(string raw, Guid expected) => Guid.TryParse(raw, out var g) && g == expected;

    private static bool NullableGuidEquals(string raw, Guid? expected)
        => Guid.TryParse(raw, out var g) && expected is { } e && g == e;

    private static bool TimestampEquals(string raw, DateTimeOffset expected)
        => DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
           && parsed.ToUnixTimeSeconds() == expected.ToUnixTimeSeconds();
}

using Dapper;
using Microsoft.EntityFrameworkCore;
using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Common.Pagination;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;
using DomainCurrency = Paybitch.Domain.Currency;

namespace Paybitch.Api.Features.Expenses;

/// <summary>
/// Handlers for the expense aggregate (§3.2, §3.2b, §3.4). Every write is behind the D6 membership
/// filter (404 for non-members), rejects an archived group (409 <c>group_archived</c>), and appends its
/// <c>expense</c> change_log row in the same transaction as the mutation (§3.5.2). Money is emitted as
/// string minor units (D1) and <c>shares</c> are server-authoritative.
/// </summary>
public static class ExpenseEndpoints
{
    private const int DefaultLimit = 25;
    private const int MaxLimit = 100;

    // ---------- CREATE (§3.2) ----------

    public static async Task<IResult> CreateAsync(
        CreateExpenseRequest request,
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

        if (await ValidateParticipantsAsync(db, groupId, request, ct) is { } participantError)
            return participantError;

        var categoryId = ParseOptionalGuid(request.CategoryId);
        if (await ValidateCategoryAsync(db, groupId, categoryId, ct) is { } categoryError)
            return categoryError;

        var paidBy = Guid.Parse(request.PaidBy!);
        var domainSplit = ExpenseMapping.ToDomainSplit(request.Split!);
        var currency = DomainCurrency.FromCode(request.Currency!);
        var amountMinor = ExpenseWire.ParseMinor(request.Amount!);
        ExpenseWire.TryParseDate(request.Date, out var date);

        var requestCanonical = ExpenseMapping.Canonicalize(
            request.Title!, amountMinor, currency.Code, paidBy, date, domainSplit,
            categoryId, request.IconSymbol, request.Notes);

        // D9 idempotency: a create replaying the same (group, clientId) is diffed, never re-applied.
        if (await FindByClientIdAsync(db, groupId, request.ClientId!, ct) is { } existing)
            return Replay(http, existing.Expense, existing.Splits, requestCanonical);

        var cmd = new ExpenseWriteCommand(
            groupId, request.ClientId, request.Title!, amountMinor, currency, paidBy, date,
            domainSplit, categoryId, request.IconSymbol, request.Notes, CreatedBy: m.UserId);

        var writer = new ExpenseWriter(db, changeLog, clock);
        var created = writer.Stage(cmd);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent create won the (group, clientId) slot — diff against the winner (D9).
            db.ChangeTracker.Clear();
            if (await FindByClientIdAsync(db, groupId, request.ClientId!, ct) is not { } raced)
                throw;
            return Replay(http, raced.Expense, raced.Splits, requestCanonical);
        }

        var splits = await SplitsOfAsync(db, created.Id, ct);
        SetETag(http, created.Version);
        return Results.Json(ExpenseMapping.ToResponse(created, splits), statusCode: StatusCodes.Status201Created);
    }

    // ---------- LIST (§3.4 cursor envelope, newest first) ----------

    public static async Task<IResult> ListAsync(
        HttpContext http,
        AppDbContext db,
        string? cursor,
        int? limit,
        CancellationToken ct)
    {
        var m = http.GetMembership();
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        DateOnly? afterDate = null;
        Guid? afterId = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!Cursor.TryDecode(cursor, out var payload) || !TryParseListCursor(payload, out var d, out var id))
                return Problems.Validation(ProblemCodes.ValidationFailed, "cursor", "Malformed cursor.");
            afterDate = d;
            afterId = id;
        }

        // Keyset over the (expense_date DESC, id DESC) index; row-value comparison uses native uuid order.
        var hasCursor = afterDate is not null;
        var sql = "SELECT id FROM expenses WHERE group_id = @groupId AND deleted_at IS NULL"
                  + (hasCursor ? " AND (expense_date, id) < (@afterDate, @afterId)" : string.Empty)
                  + " ORDER BY expense_date DESC, id DESC LIMIT @lim";
        object parameters = hasCursor
            ? new { groupId = m.GroupId, afterDate = afterDate!.Value, afterId = afterId!.Value, lim = take + 1 }
            : new { groupId = m.GroupId, lim = take + 1 };

        var conn = db.Database.GetDbConnection();
        var ids = (await conn.QueryAsync<Guid>(new CommandDefinition(sql, parameters, cancellationToken: ct))).ToList();

        var hasMore = ids.Count > take;
        var pageIds = hasMore ? ids.Take(take).ToList() : ids;
        if (pageIds.Count == 0)
            return Results.Ok(new Page<ExpenseResponse>([], new PageInfo(null, false, take)));

        var expenses = await db.Expenses.AsNoTracking()
            .Where(e => pageIds.Contains(e.Id)).ToListAsync(ct);
        var splits = await db.ExpenseSplits.AsNoTracking()
            .Where(s => pageIds.Contains(s.ExpenseId)).ToListAsync(ct);

        var byId = expenses.ToDictionary(e => e.Id);
        var splitsByExpense = splits
            .GroupBy(s => s.ExpenseId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ExpenseSplit>)g.ToList());

        var data = pageIds
            .Where(byId.ContainsKey)
            .Select(id => ExpenseMapping.ToResponse(
                byId[id],
                splitsByExpense.TryGetValue(id, out var s) ? s : []))
            .ToList();

        var last = byId[pageIds[^1]];
        var nextCursor = hasMore ? Cursor.Encode($"{ExpenseWire.Date(last.ExpenseDate)}|{last.Id}") : null;
        return Results.Ok(new Page<ExpenseResponse>(data, new PageInfo(nextCursor, hasMore, take)));
    }

    // ---------- GET detail ----------

    public static async Task<IResult> GetAsync(
        string expenseId,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var m = http.GetMembership();
        if (!Guid.TryParse(expenseId, out var eid))
            return Problems.NotFound();

        var expense = await db.Expenses.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eid && e.GroupId == m.GroupId && e.DeletedAt == null, ct);
        if (expense is null)
            return Problems.NotFound();

        var splits = await SplitsOfAsync(db, eid, ct);
        SetETag(http, expense.Version);
        return Results.Ok(ExpenseMapping.ToResponse(expense, splits));
    }

    // ---------- REPLACE (§3.2b full-aggregate PUT) ----------

    public static async Task<IResult> ReplaceAsync(
        string expenseId,
        ReplaceExpenseRequest request,
        HttpContext http,
        AppDbContext db,
        IChangeLogWriter changeLog,
        IClock clock,
        CancellationToken ct)
    {
        var m = http.GetMembership();
        var groupId = m.GroupId;
        if (!Guid.TryParse(expenseId, out var eid))
            return Problems.NotFound();

        if (await RejectIfArchivedAsync(db, groupId, ct) is { } archived)
            return archived;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var expense = await LockExpenseAsync(db, groupId, eid, ct);
        if (expense is null || expense.DeletedAt is not null)
            return Problems.NotFound();

        var ifMatch = ReadIfMatch(http);
        if (!ifMatch.Present)
            return Problems.PreconditionRequired();
        if (ifMatch.Version != expense.Version)
            return Problems.VersionConflict(ExpenseMapping.ToResponse(expense, await SplitsOfAsync(db, eid, ct)));

        if (CheckImmutableFields(request, expense) is { } immutableError)
            return immutableError;

        if (await ValidateParticipantsAsync(db, groupId, request, ct) is { } participantError)
            return participantError;

        var categoryId = ParseOptionalGuid(request.CategoryId);
        if (await ValidateCategoryAsync(db, groupId, categoryId, ct) is { } categoryError)
            return categoryError;

        var paidBy = Guid.Parse(request.PaidBy!);
        var domainSplit = ExpenseMapping.ToDomainSplit(request.Split!);
        var currency = DomainCurrency.FromCode(request.Currency!);
        var amountMinor = ExpenseWire.ParseMinor(request.Amount!);
        ExpenseWire.TryParseDate(request.Date, out var date);

        var cmd = new ExpenseWriteCommand(
            groupId, expense.ClientId, request.Title!, amountMinor, currency, paidBy, date,
            domainSplit, categoryId, request.IconSymbol, request.Notes, CreatedBy: expense.CreatedBy);
        var rows = new ExpenseWriter(db, changeLog, clock).ResolveSplits(cmd);

        // Delete-then-reinsert the split set in one tx; the deferred sum trigger validates at COMMIT.
        await db.ExpenseSplits.Where(s => s.ExpenseId == eid).ExecuteDeleteAsync(ct);
        foreach (var row in rows)
        {
            db.ExpenseSplits.Add(new ExpenseSplit
            {
                ExpenseId = eid,
                GroupMemberId = row.MemberId,
                ShareMinor = row.ShareMinor,
                Weight = row.Weight,
                BasisPoints = row.BasisPoints,
            });
        }

        expense.Title = request.Title!;
        expense.AmountMinor = amountMinor;
        expense.Currency = currency.Code;
        expense.PaidBy = paidBy;
        expense.SplitType = SplitKind.Of(domainSplit);
        expense.CategoryId = categoryId;
        expense.IconSymbol = request.IconSymbol;
        expense.ExpenseDate = date;
        expense.Notes = request.Notes;
        expense.Version += 1; // bump once per §3.2b

        changeLog.Append(groupId, ChangeLogEntityTypes.Expense, eid, isDelete: false);

        await db.SaveChangesAsync(ct);
        var newSplits = await SplitsOfAsync(db, eid, ct); // read inside the tx before commit
        await tx.CommitAsync(ct);

        SetETag(http, expense.Version);
        return Results.Ok(ExpenseMapping.ToResponse(expense, newSplits));
    }

    // ---------- DELETE (soft, §3.2b) ----------

    public static async Task<IResult> DeleteAsync(
        string expenseId,
        HttpContext http,
        AppDbContext db,
        IChangeLogWriter changeLog,
        IClock clock,
        CancellationToken ct)
    {
        var m = http.GetMembership();
        var groupId = m.GroupId;
        if (!Guid.TryParse(expenseId, out var eid))
            return Problems.NotFound();

        if (await RejectIfArchivedAsync(db, groupId, ct) is { } archived)
            return archived;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var expense = await LockExpenseAsync(db, groupId, eid, ct);
        if (expense is null || expense.DeletedAt is not null)
            return Problems.NotFound(); // replayed delete finds the tombstone → 404 (outbox treats as success)

        var ifMatch = ReadIfMatch(http);
        if (!ifMatch.Present)
            return Problems.PreconditionRequired();
        if (ifMatch.Version != expense.Version)
            return Problems.VersionConflict(ExpenseMapping.ToResponse(expense, await SplitsOfAsync(db, eid, ct)));

        expense.DeletedAt = clock.UtcNow;
        expense.Version += 1;
        changeLog.Append(groupId, ChangeLogEntityTypes.Expense, eid, isDelete: true);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        SetETag(http, expense.Version);
        return Results.NoContent();
    }

    // ---------- shared helpers ----------

    private static IResult Replay(
        HttpContext http, Expense existing, IReadOnlyList<ExpenseSplit> splits, string requestCanonical)
    {
        var existingSplit = ExpenseMapping.ReconstructSplit(existing.SplitType, splits);
        var existingCanonical = ExpenseMapping.Canonicalize(
            existing.Title, existing.AmountMinor, existing.Currency, existing.PaidBy, existing.ExpenseDate,
            existingSplit, existing.CategoryId, existing.IconSymbol, existing.Notes);

        if (!string.Equals(existingCanonical, requestCanonical, StringComparison.Ordinal))
            return Problems.ClientIdConflict(existing.Id.ToString());

        SetETag(http, existing.Version);
        return Results.Ok(ExpenseMapping.ToResponse(existing, splits));
    }

    private static async Task<IResult?> RejectIfArchivedAsync(AppDbContext db, Guid groupId, CancellationToken ct)
    {
        var archivedAt = await db.Groups.AsNoTracking()
            .Where(g => g.Id == groupId)
            .Select(g => g.ArchivedAt)
            .FirstOrDefaultAsync(ct);
        return archivedAt is not null
            ? Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.GroupArchived,
                detail: "Unarchive the group to make changes.")
            : null;
    }

    private static async Task<IResult?> ValidateParticipantsAsync(
        AppDbContext db, Guid groupId, IExpenseBody body, CancellationToken ct)
    {
        var members = await db.GroupMembers.AsNoTracking()
            .Where(x => x.GroupId == groupId && x.DeletedAt == null)
            .Select(x => x.Id)
            .ToListAsync(ct);
        var memberSet = members.ToHashSet();

        if (!memberSet.Contains(Guid.Parse(body.PaidBy!)))
            return Problems.Validation(ProblemCodes.SplitMemberInvalid, "paidBy", "paidBy is not a member of this group.");

        foreach (var memberId in SplitMemberGuids(body.Split!))
            if (!memberSet.Contains(memberId))
                return Problems.Validation(ProblemCodes.SplitMemberInvalid, "split", "A split member is not part of this group.");

        return null;
    }

    private static async Task<IResult?> ValidateCategoryAsync(
        AppDbContext db, Guid groupId, Guid? categoryId, CancellationToken ct)
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

    private static IResult? CheckImmutableFields(ReplaceExpenseRequest request, Expense expense)
    {
        var offenders = new List<ProblemError>();
        if (request.Id is not null && !GuidEquals(request.Id, expense.Id))
            offenders.Add(new ProblemError("id", "id cannot be changed."));
        if (request.GroupId is not null && !GuidEquals(request.GroupId, expense.GroupId))
            offenders.Add(new ProblemError("groupId", "groupId cannot be changed."));
        if (request.ClientId is not null && !string.Equals(request.ClientId, expense.ClientId, StringComparison.Ordinal))
            offenders.Add(new ProblemError("clientId", "clientId cannot be changed."));
        if (request.CreatedBy is not null && !NullableGuidEquals(request.CreatedBy, expense.CreatedBy))
            offenders.Add(new ProblemError("createdBy", "createdBy cannot be changed."));
        if (request.CreatedAt is not null && !TimestampEquals(request.CreatedAt, expense.CreatedAt))
            offenders.Add(new ProblemError("createdAt", "createdAt cannot be changed."));

        return offenders.Count > 0 ? Problems.Validation(ProblemCodes.ImmutableField, offenders) : null;
    }

    private static async Task<(Expense Expense, IReadOnlyList<ExpenseSplit> Splits)?> FindByClientIdAsync(
        AppDbContext db, Guid groupId, string clientId, CancellationToken ct)
    {
        var expense = await db.Expenses.AsNoTracking()
            .FirstOrDefaultAsync(e => e.GroupId == groupId && e.ClientId == clientId, ct);
        if (expense is null)
            return null;
        return (expense, await SplitsOfAsync(db, expense.Id, ct));
    }

    private static async Task<Expense?> LockExpenseAsync(AppDbContext db, Guid groupId, Guid expenseId, CancellationToken ct)
    {
        // FOR UPDATE serializes concurrent PUT/DELETE on the aggregate (D8); the row lock is held to COMMIT.
        var rows = await db.Expenses
            .FromSql($"SELECT * FROM expenses WHERE id = {expenseId} AND group_id = {groupId} FOR UPDATE")
            .ToListAsync(ct);
        return rows.FirstOrDefault();
    }

    private static async Task<IReadOnlyList<ExpenseSplit>> SplitsOfAsync(AppDbContext db, Guid expenseId, CancellationToken ct)
        => await db.ExpenseSplits.AsNoTracking().Where(s => s.ExpenseId == expenseId).ToListAsync(ct);

    private static IReadOnlyList<Guid> SplitMemberGuids(SplitDto split) => split.Type switch
    {
        SplitKind.Equal => (split.Among ?? []).Select(Guid.Parse).ToList(),
        SplitKind.Exact => (split.Amounts ?? []).Select(a => Guid.Parse(a.MemberId)).ToList(),
        SplitKind.Shares => (split.Weights ?? []).Select(w => Guid.Parse(w.MemberId)).ToList(),
        SplitKind.Percentage => (split.Percents ?? []).Select(p => Guid.Parse(p.MemberId)).ToList(),
        _ => [],
    };

    private static Guid? ParseOptionalGuid(string? raw)
        => string.IsNullOrEmpty(raw) ? null : Guid.Parse(raw);

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

    private static bool GuidEquals(string raw, Guid expected)
        => Guid.TryParse(raw, out var g) && g == expected;

    private static bool NullableGuidEquals(string raw, Guid? expected)
        => Guid.TryParse(raw, out var g) && expected is { } e && g == e;

    private static bool TimestampEquals(string raw, DateTimeOffset expected)
        => DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
           && parsed.ToUnixTimeSeconds() == expected.ToUnixTimeSeconds();

    private static bool TryParseListCursor(string payload, out DateOnly date, out Guid id)
    {
        date = default;
        id = default;
        var sep = payload.IndexOf('|');
        if (sep <= 0)
            return false;
        return ExpenseWire.TryParseDate(payload[..sep], out date)
               && Guid.TryParse(payload[(sep + 1)..], out id);
    }
}

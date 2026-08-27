using Microsoft.EntityFrameworkCore;
using Paybitch.Infrastructure;

namespace Paybitch.Api.Features.Exports;

/// <summary>One rendered expense line for a PDF statement (money kept as minor units + scale, D1/D2).</summary>
public sealed record LedgerStatementRow(
    DateOnly Date, string Currency, long AmountMinor, int Scale, string PaidByName, string? Category, string Title);

/// <summary>A per-currency total for the statement footer (never summed across currencies, D5).</summary>
public sealed record LedgerCurrencyTotal(string Currency, long TotalMinor, int Scale);

/// <summary>The materialized ledger a PDF statement renders (EXT-D4b). Bounded by group size (queued job).</summary>
public sealed record LedgerStatement(
    Guid GroupId,
    string GroupName,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<LedgerStatementRow> Rows,
    IReadOnlyList<LedgerCurrencyTotal> Totals);

/// <summary>
/// Loads a group's ledger into an in-memory <see cref="LedgerStatement"/> for the async PDF build. Unlike
/// <see cref="LedgerCsvExporter"/> (which streams), the PDF engine needs the whole document, so this
/// buffers — acceptable off the request thread in a queued job (§4.2). Money stays integer minor units +
/// the <c>currencies</c> scale (D2); nothing routes through <c>double</c>.
/// </summary>
public sealed class LedgerReader(AppDbContext db)
{
    public async Task<LedgerStatement> ReadAsync(Guid groupId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var scales = await db.Currencies.AsNoTracking()
            .ToDictionaryAsync(c => c.Code, c => c.MinorUnits, ct);

        var groupName = await db.Groups.AsNoTracking()
            .Where(g => g.Id == groupId)
            .Select(g => g.Name)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        var memberNames = await db.GroupMembers.AsNoTracking()
            .Where(m => m.GroupId == groupId)
            .Select(m => new { m.Id, m.DisplayName })
            .ToDictionaryAsync(m => m.Id, m => m.DisplayName, ct);

        var categoryNames = await db.Categories.AsNoTracking()
            .Where(c => c.GroupId == groupId || c.GroupId == null)
            .Select(c => new { c.Id, c.Name })
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var expenses = await db.Expenses.AsNoTracking()
            .Where(e => e.GroupId == groupId && e.DeletedAt == null
                        && e.ExpenseDate >= from && e.ExpenseDate <= to)
            .OrderBy(e => e.ExpenseDate).ThenBy(e => e.Id)
            .Select(e => new { e.ExpenseDate, e.Currency, e.AmountMinor, e.PaidBy, e.CategoryId, e.Title })
            .ToListAsync(ct);

        var rows = expenses.Select(e => new LedgerStatementRow(
            e.ExpenseDate,
            e.Currency,
            e.AmountMinor,
            scales.GetValueOrDefault(e.Currency, 0),
            memberNames.GetValueOrDefault(e.PaidBy, string.Empty),
            e.CategoryId is { } cid ? categoryNames.GetValueOrDefault(cid, null) : null,
            e.Title)).ToList();

        var totals = expenses
            .GroupBy(e => e.Currency, StringComparer.Ordinal)
            .Select(g => new LedgerCurrencyTotal(g.Key, g.Sum(x => x.AmountMinor), scales.GetValueOrDefault(g.Key, 0)))
            .OrderBy(t => t.Currency, StringComparer.Ordinal)
            .ToList();

        return new LedgerStatement(groupId, groupName, from, to, rows, totals);
    }
}

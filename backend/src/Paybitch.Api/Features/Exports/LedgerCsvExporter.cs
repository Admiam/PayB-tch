using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Paybitch.Infrastructure;

namespace Paybitch.Api.Features.Exports;

/// <summary>
/// Streams a group's expense ledger as a Czech/English CSV (EXT-D4a/c/j). The row engine is shared by the
/// synchronous <c>GET …/export.csv</c> endpoint (streamed straight to the response body) and the async
/// <c>kind='csv'</c> job (streamed into a blob) — one formatter, one neutralizer, one dialect.
/// <list type="bullet">
///   <item>Money: <c>amount_minor</c> is machine truth (D1, byte-exact); <c>amount</c> is the localized
///     convenience column built by <see cref="MoneyText"/> integer surgery on the <c>currencies</c> scale
///     (D2) — never through <c>double</c>.</item>
///   <item>Free text (<c>title</c>, <c>paid_by_name</c>, <c>category</c>) is formula-neutralized then
///     RFC-4180 quoted (<see cref="CsvSafe"/>, CWE-1236); machine columns are never rewritten.</item>
///   <item>Expenses are read with <c>AsAsyncEnumerable</c> so a 10 000-row ledger never buffers in memory
///     (§4.7 DoS note); the small per-group member/category/scale lookups are pre-loaded once.</item>
/// </list>
/// </summary>
public sealed class LedgerCsvExporter(AppDbContext db)
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly UTF8Encoding NoBomUtf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly string[] HeaderColumns =
    [
        "expense_id", "date", "currency", "amount_minor", "amount",
        "paid_by_member_id", "paid_by_name", "category", "split_type", "title",
    ];

    /// <summary>
    /// Write the full CSV (BOM per dialect, header, then every non-deleted expense in
    /// <paramref name="from"/>..<paramref name="to"/> inclusive) to <paramref name="output"/>. The caller
    /// owns <paramref name="output"/>'s lifetime.
    /// </summary>
    public async Task WriteAsync(
        Stream output, Guid groupId, DateOnly from, DateOnly to, CsvDialect dialect, CancellationToken ct)
    {
        var scales = await db.Currencies.AsNoTracking()
            .ToDictionaryAsync(c => c.Code, c => c.MinorUnits, ct);

        // Bounded per-group lookups (≤ MembersPerGroup members; category set is small).
        var memberNames = await db.GroupMembers.AsNoTracking()
            .Where(m => m.GroupId == groupId)
            .Select(m => new { m.Id, m.DisplayName })
            .ToDictionaryAsync(m => m.Id, m => m.DisplayName, ct);

        var categoryNames = await db.Categories.AsNoTracking()
            .Where(c => c.GroupId == groupId || c.GroupId == null)
            .Select(c => new { c.Id, c.Name })
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        if (dialect.ByteOrderMark)
            await output.WriteAsync(Utf8Bom, ct);

        await using var writer = new StreamWriter(output, NoBomUtf8, bufferSize: 1 << 16, leaveOpen: true)
        {
            NewLine = "\r\n", // RFC-4180 CRLF
        };

        await writer.WriteLineAsync(string.Join(dialect.Delimiter, HeaderColumns));

        var rows = db.Expenses.AsNoTracking()
            .Where(e => e.GroupId == groupId && e.DeletedAt == null
                        && e.ExpenseDate >= from && e.ExpenseDate <= to)
            .OrderBy(e => e.ExpenseDate).ThenBy(e => e.Id)
            .Select(e => new
            {
                e.Id, e.ExpenseDate, e.Currency, e.AmountMinor, e.PaidBy, e.CategoryId, e.SplitType, e.Title,
            })
            .AsAsyncEnumerable();

        await foreach (var e in rows.WithCancellation(ct))
        {
            var scale = scales.TryGetValue(e.Currency, out var s) ? s : 0;
            var paidByName = memberNames.GetValueOrDefault(e.PaidBy, string.Empty);
            var category = e.CategoryId is { } cid ? categoryNames.GetValueOrDefault(cid, string.Empty) : string.Empty;

            await writer.WriteLineAsync(BuildRow(
                expenseId: e.Id,
                date: e.ExpenseDate,
                currency: e.Currency,
                amountMinor: e.AmountMinor,
                scale: scale,
                paidBy: e.PaidBy,
                paidByName: paidByName,
                category: category,
                splitType: e.SplitType,
                title: e.Title,
                dialect: dialect));
        }

        await writer.FlushAsync(ct);
    }

    /// <summary>
    /// Assemble one CSV record. Machine columns are emitted verbatim (byte-exact, D1); free-text columns
    /// run through <see cref="CsvSafe.SafeFreeText"/> (neutralize → quote). Fields are joined by the
    /// dialect delimiter.
    /// </summary>
    private static string BuildRow(
        Guid expenseId, DateOnly date, string currency, long amountMinor, int scale,
        Guid paidBy, string paidByName, string category, string splitType, string title, CsvDialect dialect)
    {
        var d = dialect.Delimiter;
        var fields = new[]
        {
            // --- machine truth: numeric / enum / id — never neutralized, never (needing) quoting ---
            CsvSafe.Quote(expenseId.ToString(), d),
            CsvSafe.Quote(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d),
            CsvSafe.Quote(currency, d),
            CsvSafe.Quote(amountMinor.ToString(CultureInfo.InvariantCulture), d),
            // --- localized convenience: server-generated, no injection risk (contains only digits + sep) ---
            CsvSafe.Quote(MoneyText.Human(amountMinor, scale, dialect.DecimalSeparator), d),
            CsvSafe.Quote(paidBy.ToString(), d),
            // --- free text: formula-neutralized (EXT-D4j) then RFC-4180 quoted ---
            CsvSafe.SafeFreeText(paidByName, d),
            CsvSafe.SafeFreeText(category, d),
            CsvSafe.Quote(splitType, d),
            CsvSafe.SafeFreeText(title, d),
        };
        return string.Join(d, fields);
    }
}

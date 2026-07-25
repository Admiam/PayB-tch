using System.Globalization;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Paybitch.Infrastructure;

namespace Paybitch.Api.Features.Exports;

/// <summary>
/// On-demand per-currency spend stats (EXT-D4g, X2) — Dapper aggregates over the live ledger, no
/// materialized stats table (a stored stat is a stored balance by another name). Every aggregate is
/// <c>GROUP BY currency, …</c> so a cross-currency sum is structurally impossible (D5): CZK and EUR spend
/// never add. Money leaves as string minor units (D1); no value ever routes through <c>double</c>.
/// </summary>
/// <remarks>
/// Bucketing axis is <c>expense_date</c> (a plain local <c>date</c>, §1.3): explicit <c>from</c>/<c>to</c>
/// bucket verbatim regardless of <c>tz</c>; <c>tz</c> only resolves the open-ended upper bound ("up to
/// now"). <c>granularity</c> is an allowlist (<c>day</c>/<c>week</c>/<c>month</c>) → a fixed
/// <c>date_trunc</c> unit + <c>to_char</c> format, both passed as <b>parameters</b> (never interpolated).
/// </remarks>
public sealed class StatsService(AppDbContext db)
{
    private static readonly IReadOnlyDictionary<string, (string Unit, string Format)> GranularityMap =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["day"] = ("day", "YYYY-MM-DD"),
            ["week"] = ("week", "IYYY-\"W\"IW"), // ISO week, e.g. 2026-W27
            ["month"] = ("month", "YYYY-MM"),
        };

    /// <summary>The closed granularity allowlist (validator + handler share it).</summary>
    public static readonly IReadOnlySet<string> AllowedGranularities =
        new HashSet<string>(GranularityMap.Keys, StringComparer.Ordinal);

    public async Task<StatsResponse> ComputeAsync(
        Guid groupId, string granularity, DateOnly from, DateOnly to, string tz, CancellationToken ct)
    {
        var (unit, format) = GranularityMap[granularity];
        var conn = db.Database.GetDbConnection();
        var args = new { groupId, from, to, unit, format };

        var periodRows = (await conn.QueryAsync<PeriodRow>(new CommandDefinition(
            """
            SELECT e.currency AS Currency,
                   to_char(date_trunc(@unit, e.expense_date::timestamp), @format) AS Period,
                   SUM(e.amount_minor)::bigint AS SpendMinor
            FROM expenses e
            WHERE e.group_id = @groupId AND e.deleted_at IS NULL
              AND e.expense_date BETWEEN @from AND @to
            GROUP BY e.currency, date_trunc(@unit, e.expense_date::timestamp)
            ORDER BY e.currency, date_trunc(@unit, e.expense_date::timestamp)
            """, args, cancellationToken: ct))).ToList();

        var categoryRows = (await conn.QueryAsync<CategoryRow>(new CommandDefinition(
            """
            SELECT e.currency AS Currency,
                   e.category_id AS CategoryId,
                   SUM(e.amount_minor)::bigint AS SpendMinor
            FROM expenses e
            WHERE e.group_id = @groupId AND e.deleted_at IS NULL
              AND e.expense_date BETWEEN @from AND @to
            GROUP BY e.currency, e.category_id
            """, args, cancellationToken: ct))).ToList();

        var paidRows = (await conn.QueryAsync<MemberAmountRow>(new CommandDefinition(
            """
            SELECT e.currency AS Currency,
                   e.paid_by AS MemberId,
                   SUM(e.amount_minor)::bigint AS AmountMinor
            FROM expenses e
            WHERE e.group_id = @groupId AND e.deleted_at IS NULL
              AND e.expense_date BETWEEN @from AND @to
            GROUP BY e.currency, e.paid_by
            """, args, cancellationToken: ct))).ToList();

        var shareRows = (await conn.QueryAsync<MemberAmountRow>(new CommandDefinition(
            """
            SELECT e.currency AS Currency,
                   s.group_member_id AS MemberId,
                   SUM(s.share_minor)::bigint AS AmountMinor
            FROM expenses e
            JOIN expense_splits s ON s.expense_id = e.id
            WHERE e.group_id = @groupId AND e.deleted_at IS NULL
              AND e.expense_date BETWEEN @from AND @to
            GROUP BY e.currency, s.group_member_id
            """, args, cancellationToken: ct))).ToList();

        var buckets = Assemble(periodRows, categoryRows, paidRows, shareRows);
        return new StatsResponse(groupId.ToString(), granularity, tz, buckets, Display: null);
    }

    private static IReadOnlyList<CurrencyStatsBucket> Assemble(
        List<PeriodRow> periods, List<CategoryRow> categories,
        List<MemberAmountRow> paid, List<MemberAmountRow> shares)
    {
        // Every currency that appears in any aggregate gets a bucket (ordinal-ordered, D5-isolated).
        var currencies = periods.Select(p => p.Currency)
            .Concat(categories.Select(c => c.Currency))
            .Concat(paid.Select(p => p.Currency))
            .Concat(shares.Select(s => s.Currency))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        var periodsBy = periods.GroupBy(p => p.Currency, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var categoriesBy = categories.GroupBy(c => c.Currency, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var paidBy = paid.GroupBy(p => p.Currency, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var sharesBy = shares.GroupBy(s => s.Currency, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var result = new List<CurrencyStatsBucket>(currencies.Count);
        foreach (var currency in currencies)
        {
            var periodList = periodsBy.GetValueOrDefault(currency, []);
            var byPeriod = periodList
                .Select(p => new PeriodSpend(p.Period, Minor(p.SpendMinor)))
                .ToList();

            // totalSpend ≡ Σ byPeriod (the client-checkable invariant; equals Σ byCategory too, §4.8).
            var totalSpend = periodList.Sum(p => p.SpendMinor);

            var byCategory = categoriesBy.GetValueOrDefault(currency, [])
                .Select(c => new CategorySpend(c.CategoryId?.ToString(), Minor(c.SpendMinor)))
                .ToList();

            var byMember = MergeMembers(
                paidBy.GetValueOrDefault(currency, []),
                sharesBy.GetValueOrDefault(currency, []));

            result.Add(new CurrencyStatsBucket(currency, Minor(totalSpend), byPeriod, byCategory, byMember));
        }

        return result;
    }

    private static IReadOnlyList<MemberSpend> MergeMembers(
        List<MemberAmountRow> paid, List<MemberAmountRow> shares)
    {
        var paidByMember = paid.ToDictionary(p => p.MemberId, p => p.AmountMinor);
        var shareByMember = shares.ToDictionary(s => s.MemberId, s => s.AmountMinor);

        var memberIds = paidByMember.Keys.Concat(shareByMember.Keys).Distinct().OrderBy(id => id).ToList();
        return memberIds
            .Select(id => new MemberSpend(
                id.ToString(),
                Minor(paidByMember.GetValueOrDefault(id, 0)),
                Minor(shareByMember.GetValueOrDefault(id, 0))))
            .ToList();
    }

    private static string Minor(long minor) => minor.ToString(CultureInfo.InvariantCulture);

    // --- Dapper projection rows (SUM(...) can be NULL when a group has zero splits — never here, but
    //     the ::bigint SUM of a non-empty group is always present; empty groups yield zero rows). ---
    private sealed class PeriodRow
    {
        public string Currency { get; set; } = string.Empty;
        public string Period { get; set; } = string.Empty;
        public long SpendMinor { get; set; }
    }

    private sealed class CategoryRow
    {
        public string Currency { get; set; } = string.Empty;
        public Guid? CategoryId { get; set; }
        public long SpendMinor { get; set; }
    }

    private sealed class MemberAmountRow
    {
        public string Currency { get; set; } = string.Empty;
        public Guid MemberId { get; set; }
        public long AmountMinor { get; set; }
    }
}

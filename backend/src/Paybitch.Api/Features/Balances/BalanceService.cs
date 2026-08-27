using Microsoft.EntityFrameworkCore;
using Paybitch.Domain;
using Paybitch.Domain.Settlement;
using Paybitch.Infrastructure;

namespace Paybitch.Api.Features.Balances;

/// <summary>
/// EF-backed <see cref="IBalanceService"/>. Loads the group's non-deleted expenses (+ splits, +
/// payer) and non-voided settlements, maps them to Domain <see cref="ExpenseInput"/> /
/// <see cref="SettlementInput"/> (member ids carried as their <c>Guid</c> string), and delegates the
/// arithmetic to the pure Domain calculators (D4). Reads are <c>AsNoTracking</c> — this service never
/// writes. Member identity crosses the Domain boundary as <c>Guid.ToString()</c> and is parsed back at
/// the wire edge (the module), so the round-trip is exact.
/// </summary>
public sealed class BalanceService(AppDbContext db) : IBalanceService
{
    public async Task<GroupBalanceResult> ComputeGroupBalancesAsync(Guid groupId, CancellationToken ct = default)
    {
        var (expenses, settlements) = await LoadInputsAsync(groupId, ct);

        var byCurrency = BalanceCalculator.ComputeByCurrency(expenses, settlements);
        // Simplify over the full (zero-inclusive) balance set; zeros never produce edges, and
        // SimplifyByCurrency buckets internally so EUR is never netted against CZK (D5).
        var allBalances = byCurrency.Values.SelectMany(b => b).ToList();
        var simplified = DebtSimplifier.SimplifyByCurrency(allBalances);

        var buckets = new List<CurrencyBalances>();
        foreach (var (currency, balances) in byCurrency)
        {
            var nonZero = balances.Where(b => !b.Net.IsZero).ToList();
            if (nonZero.Count == 0)
                continue; // every member settled in this currency ⇒ omit the whole bucket

            var edges = simplified.TryGetValue(currency, out var e) ? e : Array.Empty<DebtEdge>();
            buckets.Add(new CurrencyBalances(currency, nonZero, edges));
        }

        var ordered = buckets
            .OrderBy(b => b.Currency.Code, StringComparer.Ordinal)
            .ToList();
        return new GroupBalanceResult(ordered);
    }

    public async Task<IReadOnlyList<MemberCurrencyBalance>> MemberBalancesAsync(
        Guid groupId,
        Guid memberId,
        CancellationToken ct = default)
    {
        var (expenses, settlements) = await LoadInputsAsync(groupId, ct);
        var byCurrency = BalanceCalculator.ComputeByCurrency(expenses, settlements);

        var key = memberId.ToString();
        var result = new List<MemberCurrencyBalance>();
        foreach (var (currency, balances) in byCurrency)
        {
            var mine = balances.FirstOrDefault(b => b.MemberId == key);
            if (mine is not null && !mine.Net.IsZero)
                result.Add(new MemberCurrencyBalance(currency, mine.Net));
        }

        return result
            .OrderBy(b => b.Currency.Code, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Load the canonical Domain inputs for a group. One join yields (expense × its splits); every
    /// live expense has ≥ 1 split (D3), so the inner join drops nothing. Settlements load separately.
    /// </summary>
    private async Task<(IReadOnlyList<ExpenseInput> Expenses, IReadOnlyList<SettlementInput> Settlements)>
        LoadInputsAsync(Guid groupId, CancellationToken ct)
    {
        var expenseRows = await (
            from e in db.Expenses.AsNoTracking()
            where e.GroupId == groupId && e.DeletedAt == null
            join s in db.ExpenseSplits.AsNoTracking() on e.Id equals s.ExpenseId
            select new
            {
                e.Id,
                e.PaidBy,
                e.AmountMinor,
                e.Currency,
                s.GroupMemberId,
                s.ShareMinor,
            }).ToListAsync(ct);

        var expenses = expenseRows
            .GroupBy(r => new { r.Id, r.PaidBy, r.AmountMinor, r.Currency })
            .Select(g => new ExpenseInput(
                g.Key.PaidBy.ToString(),
                new Money(g.Key.AmountMinor, Currency.FromCode(g.Key.Currency)),
                g.Select(x => (x.GroupMemberId.ToString(), x.ShareMinor)).ToList()))
            .ToList();

        var settlementRows = await db.Settlements.AsNoTracking()
            .Where(s => s.GroupId == groupId && s.DeletedAt == null)
            .Select(s => new { s.FromMember, s.ToMember, s.AmountMinor, s.Currency })
            .ToListAsync(ct);

        var settlements = settlementRows
            .Select(s => new SettlementInput(
                s.FromMember.ToString(),
                s.ToMember.ToString(),
                new Money(s.AmountMinor, Currency.FromCode(s.Currency))))
            .ToList();

        return (expenses, settlements);
    }
}

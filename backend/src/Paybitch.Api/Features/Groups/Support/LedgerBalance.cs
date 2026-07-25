using Microsoft.EntityFrameworkCore;
using Paybitch.Infrastructure;

namespace Paybitch.Api.Features.Groups.Support;

/// <summary>
/// On-demand net-balance reads for the group/member surface (D4 — no materialized store). Mirrors the
/// domain's <c>BalanceCalculator</c> sign convention exactly (§2.2):
/// <code>net(member, C) = Σ paid − Σ owed + Σ settledFrom − Σ settledTo</code>
/// <c>net &gt; 0</c> ⇒ the group owes the member. Every bucket is a single grouped aggregate — no
/// per-group / per-member N+1. Deleted expenses/settlements (<c>deleted_at</c>) are excluded; a
/// soft-deleted or ghost member row still nets (it may still hold debt — §3.8.3).
/// </summary>
public static class LedgerBalance
{
    /// <summary>
    /// Compute per-currency nets for the given member rows in one pass. Result:
    /// <c>memberId → (currencyCode → netMinor)</c>. Only currencies where the member has activity
    /// appear; an absent bucket means an implicit zero.
    /// </summary>
    public static async Task<Dictionary<Guid, Dictionary<string, long>>> ComputeNetsAsync(
        AppDbContext db, IReadOnlyCollection<Guid> memberIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, Dictionary<string, long>>();
        if (memberIds.Count == 0)
            return result;

        void Bump(Guid member, string currency, long delta)
        {
            if (!result.TryGetValue(member, out var byCurrency))
            {
                byCurrency = new Dictionary<string, long>(StringComparer.Ordinal);
                result[member] = byCurrency;
            }
            byCurrency[currency] = byCurrency.GetValueOrDefault(currency) + delta;
        }

        var paid = await db.Expenses
            .Where(e => memberIds.Contains(e.PaidBy) && e.DeletedAt == null)
            .GroupBy(e => new { e.PaidBy, e.Currency })
            .Select(g => new { g.Key.PaidBy, g.Key.Currency, Sum = g.Sum(x => x.AmountMinor) })
            .ToListAsync(ct);
        foreach (var r in paid)
            Bump(r.PaidBy, r.Currency, r.Sum);

        var owed = await (
            from es in db.ExpenseSplits
            join e in db.Expenses on es.ExpenseId equals e.Id
            where memberIds.Contains(es.GroupMemberId) && e.DeletedAt == null
            group es by new { es.GroupMemberId, e.Currency } into g
            select new { g.Key.GroupMemberId, g.Key.Currency, Sum = g.Sum(x => x.ShareMinor) })
            .ToListAsync(ct);
        foreach (var r in owed)
            Bump(r.GroupMemberId, r.Currency, -r.Sum);

        var settledFrom = await db.Settlements
            .Where(s => memberIds.Contains(s.FromMember) && s.DeletedAt == null)
            .GroupBy(s => new { s.FromMember, s.Currency })
            .Select(g => new { g.Key.FromMember, g.Key.Currency, Sum = g.Sum(x => x.AmountMinor) })
            .ToListAsync(ct);
        foreach (var r in settledFrom)
            Bump(r.FromMember, r.Currency, r.Sum);

        var settledTo = await db.Settlements
            .Where(s => memberIds.Contains(s.ToMember) && s.DeletedAt == null)
            .GroupBy(s => new { s.ToMember, s.Currency })
            .Select(g => new { g.Key.ToMember, g.Key.Currency, Sum = g.Sum(x => x.AmountMinor) })
            .ToListAsync(ct);
        foreach (var r in settledTo)
            Bump(r.ToMember, r.Currency, -r.Sum);

        return result;
    }

    /// <summary>Per-currency nets for a single member (empty when the member has no activity).</summary>
    public static async Task<Dictionary<string, long>> ComputeMemberNetAsync(
        AppDbContext db, Guid memberId, CancellationToken ct)
    {
        var nets = await ComputeNetsAsync(db, new[] { memberId }, ct);
        return nets.TryGetValue(memberId, out var byCurrency)
            ? byCurrency
            : new Dictionary<string, long>(StringComparer.Ordinal);
    }

    /// <summary>True when every currency bucket for the member is exactly zero (the §3.8.3 remove precondition).</summary>
    public static bool AllZero(IReadOnlyDictionary<string, long> nets)
        => nets.Values.All(v => v == 0);
}

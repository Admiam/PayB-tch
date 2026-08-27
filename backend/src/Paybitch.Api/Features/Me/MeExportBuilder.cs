using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;

namespace Paybitch.Api.Features.Me;

/// <summary>
/// Builds the synchronous <c>GET /me/export</c> payload (§4.4): the profile, every membership, and each
/// expense / split / settlement the user authored or participates in (via any linked member row). Money
/// stays D1 (string minor units + sibling currency). Redaction posture: only group-scoped data the user
/// can already see in-app is included — nothing new is leaked.
/// </summary>
public sealed class MeExportBuilder(AppDbContext db, IClock clock)
{
    /// <summary>Assemble the export, or null when the user row is gone.</summary>
    public async Task<ExportResponse?> BuildAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null, ct);
        if (user is null)
            return null;

        var profile = new ExportProfile(
            user.Id.ToString(), user.DisplayName, user.Email,
            user.DefaultCurrency, user.Locale, user.CreatedAt);

        // Every member row the user is (or was) linked to — the participation key.
        var myMembers = await db.GroupMembers
            .Where(m => m.UserId == userId)
            .Select(m => new { m.Id, m.GroupId, m.Role, m.CreatedAt })
            .ToListAsync(ct);
        var memberIds = myMembers.Select(m => m.Id).ToHashSet();
        var groupIds = myMembers.Select(m => m.GroupId).Distinct().ToList();

        var groupNames = await db.Groups
            .Where(g => groupIds.Contains(g.Id))
            .Select(g => new { g.Id, g.Name })
            .ToDictionaryAsync(g => g.Id, g => g.Name, ct);

        var memberships = myMembers
            .Select(m => new ExportMembership(
                m.GroupId.ToString(),
                groupNames.GetValueOrDefault(m.GroupId, string.Empty),
                m.Id.ToString(),
                m.Role,
                m.CreatedAt))
            .ToList();

        var expenses = await BuildExpensesAsync(userId, memberIds, ct);
        var settlements = await BuildSettlementsAsync(userId, memberIds, ct);

        return new ExportResponse(clock.UtcNow, profile, memberships, expenses, settlements);
    }

    private async Task<IReadOnlyList<ExportExpense>> BuildExpensesAsync(
        Guid userId, HashSet<Guid> memberIds, CancellationToken ct)
    {
        // Expenses the user authored or paid, plus those they hold a split slot in.
        var authoredOrPaid = await db.Expenses
            .Where(e => e.DeletedAt == null && (e.CreatedBy == userId || memberIds.Contains(e.PaidBy)))
            .Select(e => e.Id)
            .ToListAsync(ct);
        var viaSplit = await db.ExpenseSplits
            .Where(s => memberIds.Contains(s.GroupMemberId))
            .Select(s => s.ExpenseId)
            .Distinct()
            .ToListAsync(ct);

        var expenseIds = authoredOrPaid.Concat(viaSplit).ToHashSet();
        if (expenseIds.Count == 0)
            return [];

        var rows = await db.Expenses
            .Where(e => expenseIds.Contains(e.Id) && e.DeletedAt == null)
            .OrderByDescending(e => e.ExpenseDate)
            .ToListAsync(ct);
        var splits = await db.ExpenseSplits
            .Where(s => expenseIds.Contains(s.ExpenseId))
            .ToListAsync(ct);
        var splitsByExpense = splits.GroupBy(s => s.ExpenseId).ToDictionary(g => g.Key, g => g.ToList());

        return rows.Select(e =>
        {
            var expenseSplits = splitsByExpense.GetValueOrDefault(e.Id, []);
            var myShare = expenseSplits
                .Where(s => memberIds.Contains(s.GroupMemberId))
                .Select(s => (long?)s.ShareMinor)
                .FirstOrDefault();

            return new ExportExpense(
                e.GroupId.ToString(),
                e.Id.ToString(),
                e.Title,
                Minor(e.AmountMinor),
                e.Currency,
                e.PaidBy.ToString(),
                e.ExpenseDate,
                myShare is { } share ? Minor(share) : null,
                e.CreatedBy?.ToString(),
                expenseSplits.Select(s => new ExportSplit(s.GroupMemberId.ToString(), Minor(s.ShareMinor))).ToList());
        }).ToList();
    }

    private async Task<IReadOnlyList<ExportSettlement>> BuildSettlementsAsync(
        Guid userId, HashSet<Guid> memberIds, CancellationToken ct)
    {
        var rows = await db.Settlements
            .Where(s => s.DeletedAt == null
                        && (s.CreatedBy == userId || memberIds.Contains(s.FromMember) || memberIds.Contains(s.ToMember)))
            .OrderByDescending(s => s.SettledOn)
            .ToListAsync(ct);

        return rows.Select(s => new ExportSettlement(
            s.GroupId.ToString(),
            s.Id.ToString(),
            s.FromMember.ToString(),
            s.ToMember.ToString(),
            Minor(s.AmountMinor),
            s.Currency,
            s.SettledOn)).ToList();
    }

    private static string Minor(long amount) => amount.ToString(CultureInfo.InvariantCulture);
}

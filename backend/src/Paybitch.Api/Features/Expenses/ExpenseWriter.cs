using Paybitch.Domain;
using Paybitch.Domain.Splitting;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;
using DomainCurrency = Paybitch.Domain.Currency;

namespace Paybitch.Api.Features.Expenses;

/// <summary>
/// A fully validated intent to persist an expense aggregate. The <see cref="Split"/> is the domain
/// tagged union with member ids already normalized to Guid strings; <see cref="Splitter"/> turns it into
/// the authoritative shares. Reused by both the create endpoint and (Phase C) recurring materialization.
/// </summary>
public sealed record ExpenseWriteCommand(
    Guid GroupId,
    string? ClientId,
    string Title,
    long AmountMinor,
    DomainCurrency Currency,
    Guid PaidBy,
    DateOnly Date,
    SplitType Split,
    Guid? CategoryId,
    string? IconSymbol,
    string? Notes,
    Guid? CreatedBy,
    Guid? RecurringRuleId = null);

/// <summary>One resolved participant row: the owed <c>share_minor</c> plus the audited split inputs.</summary>
public readonly record struct ResolvedSplitRow(Guid MemberId, long ShareMinor, int? Weight, int? BasisPoints);

/// <summary>
/// The single core that turns an <see cref="ExpenseWriteCommand"/> into a persisted expense + split rows
/// + an <c>expense</c> change_log entry, all staged on the caller's DbContext so they commit in one
/// transaction (§3.5.2). This is the shared write path the recurring extension (Phase C) materializes
/// through — construct it with the request-scoped <see cref="AppDbContext"/>, <see cref="IChangeLogWriter"/>
/// and <see cref="IClock"/>. It never calls <c>SaveChanges</c>: the caller owns the transaction so it can
/// wrap the D9 idempotency race or the recurring batch as it sees fit.
/// </summary>
public sealed class ExpenseWriter(AppDbContext db, IChangeLogWriter changeLog, IClock clock)
{
    /// <summary>
    /// Resolve the split to authoritative shares (largest-remainder, §2.3) and pair each with its audit
    /// input (<c>weight</c> for SHARES, <c>basis_points</c> for PERCENTAGE; both null otherwise). Pure —
    /// no DbContext touch — so the replace path can reuse it before deleting/reinserting split rows.
    /// </summary>
    public IReadOnlyList<ResolvedSplitRow> ResolveSplits(ExpenseWriteCommand cmd)
    {
        var amount = new Money(cmd.AmountMinor, cmd.Currency);
        var shares = Splitter.Resolve(amount, cmd.Split);
        var weights = WeightInputs(cmd.Split);
        var basisPoints = BasisPointInputs(cmd.Split);

        return shares
            .Select(s =>
            {
                var memberId = Guid.Parse(s.MemberId);
                return new ResolvedSplitRow(
                    memberId,
                    s.Share.Minor,
                    weights.TryGetValue(s.MemberId, out var w) ? w : null,
                    basisPoints.TryGetValue(s.MemberId, out var bp) ? bp : null);
            })
            .ToList();
    }

    /// <summary>
    /// Stage the whole aggregate: a new <see cref="Expense"/> (version 1), its <see cref="ExpenseSplit"/>
    /// rows, and the <c>expense</c> upsert change_log row. Returns the tracked expense (id already
    /// assigned) so the caller can build the response after <c>SaveChanges</c>.
    /// </summary>
    public Expense Stage(ExpenseWriteCommand cmd)
    {
        var rows = ResolveSplits(cmd);
        var now = clock.UtcNow;

        var expense = new Expense
        {
            Id = Guid.CreateVersion7(),
            GroupId = cmd.GroupId,
            ClientId = cmd.ClientId,
            Title = cmd.Title,
            AmountMinor = cmd.AmountMinor,
            Currency = cmd.Currency.Code,
            PaidBy = cmd.PaidBy,
            SplitType = SplitKind.Of(cmd.Split),
            CategoryId = cmd.CategoryId,
            IconSymbol = cmd.IconSymbol,
            ExpenseDate = cmd.Date,
            Notes = cmd.Notes,
            Version = 1,
            CreatedBy = cmd.CreatedBy,
            RecurringRuleId = cmd.RecurringRuleId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Expenses.Add(expense);

        foreach (var row in rows)
        {
            db.ExpenseSplits.Add(new ExpenseSplit
            {
                ExpenseId = expense.Id,
                GroupMemberId = row.MemberId,
                ShareMinor = row.ShareMinor,
                Weight = row.Weight,
                BasisPoints = row.BasisPoints,
            });
        }

        changeLog.Append(cmd.GroupId, ChangeLogEntityTypes.Expense, expense.Id, isDelete: false);
        return expense;
    }

    private static IReadOnlyDictionary<string, int> WeightInputs(SplitType split) =>
        split is SplitType.Shares sh
            ? sh.Weights.ToDictionary(w => w.MemberId, w => w.Weight, StringComparer.Ordinal)
            : EmptyInputs;

    private static IReadOnlyDictionary<string, int> BasisPointInputs(SplitType split) =>
        split is SplitType.Percentage pc
            ? pc.Percents.ToDictionary(p => p.MemberId, p => p.BasisPoints, StringComparer.Ordinal)
            : EmptyInputs;

    private static readonly IReadOnlyDictionary<string, int> EmptyInputs =
        new Dictionary<string, int>(StringComparer.Ordinal);
}

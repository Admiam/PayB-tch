using System.Text.Json;
using Paybitch.Domain.Splitting;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Expenses;

/// <summary>
/// Wire ↔ domain ↔ persistence mapping for the split tagged union, the response representation, and the
/// D9 canonical form. Member ids are always normalized to their lowercase Guid string so the domain
/// split, the persisted rows, and the idempotency canonical all agree byte-for-byte.
/// </summary>
public static class ExpenseMapping
{
    // ---- wire DTO → domain (post-validation; ids are known-good Guids) ----

    /// <summary>Build the domain <see cref="SplitType"/> from a validated <see cref="SplitDto"/>.</summary>
    public static SplitType ToDomainSplit(SplitDto dto) => dto.Type switch
    {
        SplitKind.Equal => new SplitType.Equal(
            (dto.Among ?? []).Select(NormId).ToList()),
        SplitKind.Exact => new SplitType.Exact(
            (dto.Amounts ?? []).Select(a => (NormId(a.MemberId), ExpenseWire.ParseMinor(a.Amount))).ToList()),
        SplitKind.Shares => new SplitType.Shares(
            (dto.Weights ?? []).Select(w => (NormId(w.MemberId), w.Weight)).ToList()),
        SplitKind.Percentage => new SplitType.Percentage(
            (dto.Percents ?? []).Select(p => (NormId(p.MemberId), p.BasisPoints)).ToList()),
        _ => throw new Paybitch.Domain.InvalidSplitException($"Unknown split type '{dto.Type}'."),
    };

    // ---- persistence → domain (reconstruct the split from expense + split rows) ----

    /// <summary>Rebuild the domain <see cref="SplitType"/> from the stored split rows (audit inputs).</summary>
    public static SplitType ReconstructSplit(string kind, IReadOnlyList<ExpenseSplit> splits)
    {
        var ordered = splits.OrderBy(s => s.GroupMemberId).ToList();
        return kind switch
        {
            SplitKind.Equal => new SplitType.Equal(
                ordered.Select(s => s.GroupMemberId.ToString()).ToList()),
            SplitKind.Exact => new SplitType.Exact(
                ordered.Select(s => (s.GroupMemberId.ToString(), s.ShareMinor)).ToList()),
            SplitKind.Shares => new SplitType.Shares(
                ordered.Select(s => (s.GroupMemberId.ToString(), s.Weight ?? 0)).ToList()),
            SplitKind.Percentage => new SplitType.Percentage(
                ordered.Select(s => (s.GroupMemberId.ToString(), s.BasisPoints ?? 0)).ToList()),
            _ => throw new Paybitch.Domain.InvalidSplitException($"Unknown split type '{kind}'."),
        };
    }

    // ---- domain → wire DTO (tagged union echoed back on the response) ----

    /// <summary>Project the domain split to its wire DTO (members sorted by id for determinism).</summary>
    public static SplitDto ToSplitDto(SplitType split) => split switch
    {
        SplitType.Equal e => new SplitDto
        {
            Type = SplitKind.Equal,
            Among = e.Among.OrderBy(x => x, StringComparer.Ordinal).ToList(),
        },
        SplitType.Exact ex => new SplitDto
        {
            Type = SplitKind.Exact,
            Amounts = ex.Amounts.OrderBy(a => a.MemberId, StringComparer.Ordinal)
                .Select(a => new ExactSplitEntryDto(a.MemberId, ExpenseWire.Minor(a.ShareMinor))).ToList(),
        },
        SplitType.Shares sh => new SplitDto
        {
            Type = SplitKind.Shares,
            Weights = sh.Weights.OrderBy(w => w.MemberId, StringComparer.Ordinal)
                .Select(w => new ShareSplitEntryDto(w.MemberId, w.Weight)).ToList(),
        },
        SplitType.Percentage pc => new SplitDto
        {
            Type = SplitKind.Percentage,
            Percents = pc.Percents.OrderBy(p => p.MemberId, StringComparer.Ordinal)
                .Select(p => new PercentSplitEntryDto(p.MemberId, p.BasisPoints)).ToList(),
        },
        _ => throw new Paybitch.Domain.InvalidSplitException("Unknown split type."),
    };

    /// <summary>Assemble the §3.2 representation from an expense aggregate (row + its splits).</summary>
    public static ExpenseResponse ToResponse(Expense e, IReadOnlyList<ExpenseSplit> splits)
    {
        var split = ReconstructSplit(e.SplitType, splits);
        var shares = splits
            .OrderBy(s => s.GroupMemberId)
            .Select(s => new ShareDto(s.GroupMemberId.ToString(), ExpenseWire.Minor(s.ShareMinor)))
            .ToList();

        return new ExpenseResponse(
            Id: e.Id.ToString(),
            ClientId: e.ClientId,
            GroupId: e.GroupId.ToString(),
            Title: e.Title,
            Amount: ExpenseWire.Minor(e.AmountMinor),
            Currency: e.Currency,
            PaidBy: e.PaidBy.ToString(),
            Date: ExpenseWire.Date(e.ExpenseDate),
            Split: ToSplitDto(split),
            Shares: shares,
            CategoryId: e.CategoryId?.ToString(),
            IconSymbol: e.IconSymbol,
            Notes: e.Notes,
            CreatedBy: e.CreatedBy?.ToString(),
            CreatedAt: ExpenseWire.Timestamp(e.CreatedAt),
            Version: e.Version,
            Deleted: e.DeletedAt is not null);
    }

    // ---- D9 canonical form (§3.4) ----

    private static readonly JsonSerializerOptions CanonicalOptions = new();

    /// <summary>
    /// The canonical, byte-comparable form over the idempotency fields (§3.4): <c>title</c>, <c>amount</c>,
    /// <c>currency</c>, <c>paidBy</c>, <c>date</c>, <c>split</c> (type + membership, members sorted by id),
    /// <c>categoryId</c>, <c>iconSymbol</c>, <c>notes</c> — absent optional ≡ null. Server-set fields are
    /// excluded. Identical content ⇒ identical string; any divergence ⇒ 409 <c>client_id_conflict</c>.
    /// </summary>
    public static string Canonicalize(
        string title,
        long amountMinor,
        string currency,
        Guid paidBy,
        DateOnly date,
        SplitType split,
        Guid? categoryId,
        string? iconSymbol,
        string? notes)
    {
        object canonicalSplit = split switch
        {
            SplitType.Equal e => new
            {
                type = SplitKind.Equal,
                members = e.Among.OrderBy(x => x, StringComparer.Ordinal)
                    .Select(m => new { memberId = m }).ToArray(),
            },
            SplitType.Exact ex => new
            {
                type = SplitKind.Exact,
                members = ex.Amounts.OrderBy(a => a.MemberId, StringComparer.Ordinal)
                    .Select(a => new { memberId = a.MemberId, amount = a.ShareMinor }).ToArray(),
            },
            SplitType.Shares sh => new
            {
                type = SplitKind.Shares,
                members = sh.Weights.OrderBy(w => w.MemberId, StringComparer.Ordinal)
                    .Select(w => new { memberId = w.MemberId, weight = w.Weight }).ToArray(),
            },
            SplitType.Percentage pc => new
            {
                type = SplitKind.Percentage,
                members = pc.Percents.OrderBy(p => p.MemberId, StringComparer.Ordinal)
                    .Select(p => new { memberId = p.MemberId, basisPoints = p.BasisPoints }).ToArray(),
            },
            _ => new { type = "unknown", members = Array.Empty<object>() },
        };

        var canonical = new
        {
            title,
            amount = amountMinor,
            currency,
            paidBy = paidBy.ToString(),
            date = ExpenseWire.Date(date),
            categoryId = categoryId?.ToString(),
            iconSymbol,
            notes,
            split = canonicalSplit,
        };

        return JsonSerializer.Serialize(canonical, CanonicalOptions);
    }

    private static string NormId(string memberId) => Guid.Parse(memberId).ToString();
}

using System.Text.Json;
using Paybitch.Api.Features.Expenses;
using Paybitch.Domain.Splitting;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Recurring;

/// <summary>
/// Wire ↔ persistence ↔ domain mapping for a recurring rule: the split template (reusing the Expenses
/// tagged union, D3), the response representation, the fire-time domain <see cref="SplitType"/>, and the
/// D9 canonical form. Member ids are normalized to their lowercase Guid string so template rows, the fired
/// expense, and the idempotency canonical all agree byte-for-byte.
/// </summary>
public static class RecurringMapping
{
    /// <summary>Dynamic-equal ⇔ split type is <c>equal</c> with NO member list (EXT-D3d).</summary>
    public static bool IsDynamicEqual(SplitDto split) =>
        split.Type == SplitKind.Equal && (split.Among is null || split.Among.Count == 0);

    /// <summary>The four persisted split-template rows for a rule; EMPTY for dynamic-equal (no rows = dynamic).</summary>
    public static IReadOnlyList<RecurringRuleSplit> BuildSplitRows(Guid ruleId, SplitDto split)
    {
        if (IsDynamicEqual(split))
            return [];

        return split.Type switch
        {
            SplitKind.Equal => (split.Among ?? [])
                .Select(m => new RecurringRuleSplit { RuleId = ruleId, GroupMemberId = NormId(m) })
                .ToList(),
            SplitKind.Exact => (split.Amounts ?? [])
                .Select(a => new RecurringRuleSplit
                {
                    RuleId = ruleId,
                    GroupMemberId = NormId(a.MemberId),
                    AmountMinor = ExpenseWire.ParseMinor(a.Amount),
                }).ToList(),
            SplitKind.Shares => (split.Weights ?? [])
                .Select(w => new RecurringRuleSplit
                {
                    RuleId = ruleId,
                    GroupMemberId = NormId(w.MemberId),
                    Weight = w.Weight,
                }).ToList(),
            SplitKind.Percentage => (split.Percents ?? [])
                .Select(p => new RecurringRuleSplit
                {
                    RuleId = ruleId,
                    GroupMemberId = NormId(p.MemberId),
                    BasisPoints = p.BasisPoints,
                }).ToList(),
            _ => [],
        };
    }

    /// <summary>
    /// The fire-time domain split (EXT-D3d): dynamic-equal resolves to an <c>equal</c> over the supplied
    /// active-member set; a pinned rule reconstructs its frozen allocation from the template rows.
    /// </summary>
    public static SplitType BuildFireSplit(
        RecurringRule rule, IReadOnlyList<RecurringRuleSplit> rows, IReadOnlyList<Guid> activeMemberIds)
    {
        if (rule.SplitType == SplitKind.Equal && rows.Count == 0)
            return new SplitType.Equal(activeMemberIds.OrderBy(x => x).Select(x => x.ToString()).ToList());

        var ordered = rows.OrderBy(r => r.GroupMemberId).ToList();
        return rule.SplitType switch
        {
            SplitKind.Equal => new SplitType.Equal(
                ordered.Select(r => r.GroupMemberId.ToString()).ToList()),
            SplitKind.Exact => new SplitType.Exact(
                ordered.Select(r => (r.GroupMemberId.ToString(), r.AmountMinor ?? 0L)).ToList()),
            SplitKind.Shares => new SplitType.Shares(
                ordered.Select(r => (r.GroupMemberId.ToString(), r.Weight ?? 0)).ToList()),
            SplitKind.Percentage => new SplitType.Percentage(
                ordered.Select(r => (r.GroupMemberId.ToString(), r.BasisPoints ?? 0)).ToList()),
            _ => throw new Paybitch.Domain.InvalidSplitException($"Unknown split type '{rule.SplitType}'."),
        };
    }

    // ---- response ----

    public static RecurringRuleResponse ToResponse(RecurringRule r, IReadOnlyList<RecurringRuleSplit> rows) =>
        new(
            Id: r.Id.ToString(),
            ClientId: r.ClientId,
            GroupId: r.GroupId.ToString(),
            Title: r.Title,
            Amount: ExpenseWire.Minor(r.AmountMinor),
            Currency: r.Currency,
            PaidBy: r.PaidBy.ToString(),
            CategoryId: r.CategoryId?.ToString(),
            IconSymbol: r.IconSymbol,
            Notes: r.Notes,
            Recurrence: new RecurrenceResponse(
                Freq: r.Freq,
                Interval: r.Interval,
                ByMonthDay: r.ByMonthDay,
                ByWeekday: r.ByWeekday,
                Timezone: r.Timezone,
                StartsOn: ExpenseWire.Date(r.StartsOn),
                EndsOn: r.EndsOn is { } e ? ExpenseWire.Date(e) : null,
                RemainingCount: r.RemainingCount),
            Split: ToSplitResponse(r, rows),
            Status: r.Status,
            PauseReason: r.PauseReason,
            NextRunAt: r.NextRunAt is { } n ? ExpenseWire.Timestamp(n) : null,
            LastRunAt: r.LastRunAt is { } l ? ExpenseWire.Timestamp(l) : null,
            CreatedBy: r.CreatedBy?.ToString(),
            CreatedAt: ExpenseWire.Timestamp(r.CreatedAt),
            Version: r.Version,
            Deleted: r.DeletedAt is not null);

    private static RecurringSplitResponse ToSplitResponse(RecurringRule r, IReadOnlyList<RecurringRuleSplit> rows)
    {
        if (r.SplitType == SplitKind.Equal && rows.Count == 0)
            return new RecurringSplitResponse { Type = SplitKind.Equal, Dynamic = true };

        var ordered = rows.OrderBy(x => x.GroupMemberId).ToList();
        return r.SplitType switch
        {
            SplitKind.Equal => new RecurringSplitResponse
            {
                Type = SplitKind.Equal,
                Among = ordered.Select(x => x.GroupMemberId.ToString()).ToList(),
            },
            SplitKind.Exact => new RecurringSplitResponse
            {
                Type = SplitKind.Exact,
                Amounts = ordered
                    .Select(x => new ExactSplitEntryDto(x.GroupMemberId.ToString(), ExpenseWire.Minor(x.AmountMinor ?? 0)))
                    .ToList(),
            },
            SplitKind.Shares => new RecurringSplitResponse
            {
                Type = SplitKind.Shares,
                Weights = ordered
                    .Select(x => new ShareSplitEntryDto(x.GroupMemberId.ToString(), x.Weight ?? 0))
                    .ToList(),
            },
            SplitKind.Percentage => new RecurringSplitResponse
            {
                Type = SplitKind.Percentage,
                Percents = ordered
                    .Select(x => new PercentSplitEntryDto(x.GroupMemberId.ToString(), x.BasisPoints ?? 0))
                    .ToList(),
            },
            _ => new RecurringSplitResponse { Type = r.SplitType },
        };
    }

    // ---- D9 canonical form (§3.4) ----

    private static readonly JsonSerializerOptions CanonicalOptions = new();

    /// <summary>
    /// A byte-comparable canonical over the idempotency fields (business body + recurrence + split template).
    /// Identical content ⇒ identical string; any divergence on a replayed <c>clientId</c> ⇒ 409
    /// <c>client_id_conflict</c>. Server-set fields (status/version/runAt/…) are excluded.
    /// </summary>
    public static string Canonicalize(RecurringRule rule, IReadOnlyList<RecurringRuleSplit> rows)
    {
        object split = rule.SplitType == SplitKind.Equal && rows.Count == 0
            ? new { type = SplitKind.Equal, dynamic = true }
            : new
            {
                type = rule.SplitType,
                members = rows.OrderBy(r => r.GroupMemberId).Select(r => new
                {
                    memberId = r.GroupMemberId.ToString(),
                    weight = r.Weight,
                    basisPoints = r.BasisPoints,
                    amount = r.AmountMinor,
                }).ToArray(),
            };

        var canonical = new
        {
            title = rule.Title,
            amount = rule.AmountMinor,
            currency = rule.Currency,
            paidBy = rule.PaidBy.ToString(),
            categoryId = rule.CategoryId?.ToString(),
            iconSymbol = rule.IconSymbol,
            notes = rule.Notes,
            recurrence = new
            {
                freq = rule.Freq,
                interval = rule.Interval,
                byMonthDay = rule.ByMonthDay,
                byWeekday = rule.ByWeekday,
                timezone = rule.Timezone,
                startsOn = ExpenseWire.Date(rule.StartsOn),
                endsOn = rule.EndsOn is { } e ? ExpenseWire.Date(e) : null,
                remainingCount = rule.RemainingCount,
            },
            split,
        };

        return JsonSerializer.Serialize(canonical, CanonicalOptions);
    }

    private static Guid NormId(string memberId) => Guid.Parse(memberId);
}

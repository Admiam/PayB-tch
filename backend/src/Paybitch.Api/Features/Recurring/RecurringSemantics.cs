using Paybitch.Api.Common.Errors;
using Paybitch.Api.Features.Expenses;

namespace Paybitch.Api.Features.Recurring;

/// <summary>The validated recurrence columns produced by <see cref="RecurringSemantics.TryParseRecurrence"/>.</summary>
public readonly record struct ParsedRecurrence(
    string Freq,
    int Interval,
    int? ByMonthDay,
    int? ByWeekday,
    string Timezone,
    DateOnly StartsOn,
    DateOnly? EndsOn,
    int? RemainingCount);

/// <summary>
/// Cross-field boundary checks (§3.4/§3.7) that own the E3-specific 422 codes. Pure (no DbContext): member
/// existence, category, and the active-rule cap are DB-scoped and stay in the handler. Each method returns a
/// non-null <see cref="IResult"/> problem on failure or null on success — the exact wire <c>code</c> comes
/// straight from <c>Problems.Validation</c>, bypassing the validation-filter twin table (which does not yet
/// know these additive codes).
/// </summary>
public static class RecurringSemantics
{
    /// <summary>Parse + validate the recurrence shape (EXT-D3a): freq/interval/by_* columns, tz, end bound XOR.</summary>
    public static IResult? TryParseRecurrence(RecurrenceDto? dto, out ParsedRecurrence parsed)
    {
        parsed = default;
        if (dto is null)
            return Problems.Validation(RecurringProblemCodes.InvalidRecurrence, "recurrence", "A recurrence is required.");

        if (!Freq.IsValid(dto.Freq))
            return Invalid("recurrence.freq", "freq must be one of weekly, monthly, yearly.");

        var interval = dto.Interval ?? 1;
        if (interval < RecurringLimits.IntervalMin || interval > RecurringLimits.IntervalMax)
            return Invalid("recurrence.interval", "interval must be between 1 and 60.");

        if (!RecurrenceEngine.TryResolveTimeZone(dto.Timezone, out _))
            return Problems.Validation(RecurringProblemCodes.InvalidTimezone, "recurrence.timezone",
                "timezone must be a valid IANA tzdb identifier.");

        if (!ExpenseWire.TryParseDate(dto.StartsOn, out var startsOn))
            return Invalid("recurrence.startsOn", "startsOn must be a valid ISO date (yyyy-MM-dd).");

        DateOnly? endsOn = null;
        if (dto.EndsOn is not null)
        {
            if (!ExpenseWire.TryParseDate(dto.EndsOn, out var e))
                return Invalid("recurrence.endsOn", "endsOn must be a valid ISO date (yyyy-MM-dd).");
            if (e < startsOn)
                return Invalid("recurrence.endsOn", "endsOn must be on or after startsOn.");
            endsOn = e;
        }

        if (endsOn is not null && dto.RemainingCount is not null)
            return Problems.Validation(RecurringProblemCodes.RecurrenceEndAmbiguous, "recurrence",
                "Supply at most one of endsOn or remainingCount.");

        if (dto.RemainingCount is < 0)
            return Invalid("recurrence.remainingCount", "remainingCount must be zero or greater.");

        // Per-freq shape guards (mirror the DB CHECKs, EXT-D3a).
        int? byMonthDay = null;
        int? byWeekday = null;
        switch (dto.Freq)
        {
            case Freq.Weekly:
                if (dto.ByWeekday is not (>= 1 and <= 7) || dto.ByMonthDay is not null)
                    return Invalid("recurrence", "weekly requires byWeekday (1–7) and no byMonthDay.");
                byWeekday = dto.ByWeekday;
                break;
            case Freq.Monthly:
                if (dto.ByMonthDay is not (>= 1 and <= 31) || dto.ByWeekday is not null)
                    return Invalid("recurrence", "monthly requires byMonthDay (1–31) and no byWeekday.");
                byMonthDay = dto.ByMonthDay;
                break;
            case Freq.Yearly:
                if (dto.ByWeekday is not null || dto.ByMonthDay is not null)
                    return Invalid("recurrence", "yearly takes neither byWeekday nor byMonthDay (anchored on startsOn).");
                break;
        }

        parsed = new ParsedRecurrence(dto.Freq!, interval, byMonthDay, byWeekday, dto.Timezone!, startsOn,
            endsOn, dto.RemainingCount);
        return null;
    }

    /// <summary>
    /// Validate the split template (EXT-D3d): dynamic-equal (memberless) is allowed only for <c>equal</c>;
    /// exact/shares/percentage require pinned member rows; every pinned member must be an active member; and
    /// the type-specific sum invariants hold (reusing the Expenses twin codes).
    /// </summary>
    public static IResult? ValidateSplit(SplitDto? split, IReadOnlySet<Guid> activeMembers, long amountMinor)
    {
        if (split is null)
            return Problems.Validation(ProblemCodes.InvalidSplitType, "split", "A split is required.");

        switch (split.Type)
        {
            case SplitKind.Equal:
                if (split.Among is null || split.Among.Count == 0)
                    return null; // dynamic-equal — no member set (EXT-D3d)
                return ValidateMembers(split.Among, activeMembers);

            case SplitKind.Exact:
            {
                var entries = split.Amounts;
                if (entries is null || entries.Count == 0)
                    return SplitRequired("exact");
                if (ValidateMembers(entries.Select(e => e.MemberId).ToList(), activeMembers) is { } bad)
                    return bad;
                long sum = 0;
                foreach (var e in entries)
                {
                    if (!ExpenseWire.IsCanonicalNonNegativeMinor(e.Amount))
                        return Problems.Validation(ProblemCodes.ShareNegative, "split",
                            "exact amounts must be non-negative integer minor units.");
                    sum += ExpenseWire.ParseMinor(e.Amount);
                }
                if (sum != amountMinor)
                    return Problems.Validation(ProblemCodes.SplitSumMismatch, "split",
                        "exact amounts must sum to the rule amount.");
                return null;
            }

            case SplitKind.Shares:
            {
                var entries = split.Weights;
                if (entries is null || entries.Count == 0)
                    return SplitRequired("shares");
                if (ValidateMembers(entries.Select(e => e.MemberId).ToList(), activeMembers) is { } bad)
                    return bad;
                if (entries.Any(e => e.Weight < 1))
                    return Problems.Validation(ProblemCodes.InvalidWeight, "split", "each shares weight must be >= 1.");
                return null;
            }

            case SplitKind.Percentage:
            {
                var entries = split.Percents;
                if (entries is null || entries.Count == 0)
                    return SplitRequired("percentage");
                if (ValidateMembers(entries.Select(e => e.MemberId).ToList(), activeMembers) is { } bad)
                    return bad;
                if (entries.Any(e => e.BasisPoints < 1))
                    return Problems.Validation(ProblemCodes.InvalidBasisPoints, "split", "each basisPoints must be >= 1.");
                if (entries.Sum(e => (long)e.BasisPoints) != 10_000)
                    return Problems.Validation(ProblemCodes.SplitPercentSumMismatch, "split",
                        "percentage basisPoints must sum to 10000.");
                return null;
            }

            default:
                return Problems.Validation(ProblemCodes.InvalidSplitType, "split", "Unknown split type.");
        }
    }

    private static IResult? ValidateMembers(IReadOnlyList<string> ids, IReadOnlySet<Guid> activeMembers)
    {
        var seen = new HashSet<Guid>();
        foreach (var raw in ids)
        {
            if (!Guid.TryParse(raw, out var g))
                return Problems.Validation(ProblemCodes.SplitMemberInvalid, "split", "Split members must be valid ids.");
            if (!seen.Add(g))
                return Problems.Validation(ProblemCodes.SplitMemberInvalid, "split", "Split members must be unique.");
            if (!activeMembers.Contains(g))
                return Problems.Validation(ProblemCodes.SplitMemberInvalid, "split", "A split member is not part of this group.");
        }
        return null;
    }

    private static IResult SplitRequired(string type) =>
        Problems.Validation(RecurringProblemCodes.RecurringSplitRequired, "split",
            $"{type} split requires an explicit member list (only equal may be dynamic).");

    private static IResult Invalid(string field, string message) =>
        Problems.Validation(RecurringProblemCodes.InvalidRecurrence, field, message);
}

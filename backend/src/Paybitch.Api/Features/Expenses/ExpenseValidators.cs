using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Options;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Common.Options;
using Paybitch.Api.Features.Recurring;
using Paybitch.Infrastructure.Abstractions;

namespace Paybitch.Api.Features.Expenses;

/// <summary>Boundary twin (§3.4) for <c>POST /groups/{g}/expenses</c>.</summary>
public sealed class CreateExpenseRequestValidator : AbstractValidator<CreateExpenseRequest>
{
    public CreateExpenseRequestValidator(IClock clock, IOptions<OperationalConstants> ops)
    {
        RuleFor(x => x.ClientId)
            .Must(c => !string.IsNullOrWhiteSpace(c) && c!.Length <= 200)
            .WithMessage("clientId is required and must be at most 200 characters.");

        // Reserve the rec: namespace (EXT-D3f): only the materialization worker mints deterministic
        // clientIds under this prefix, so a user-supplied one must be rejected up front.
        RuleFor(x => x.ClientId)
            .Must(c => c is null || !c.StartsWith(RecurringLimits.FiredClientIdPrefix, StringComparison.Ordinal))
            .WithErrorCode(ProblemCodes.ReservedClientId)
            .WithMessage($"clientId must not start with the reserved '{RecurringLimits.FiredClientIdPrefix}' prefix.");

        ExpenseBodyRules.Apply(this, clock, ops.Value.NotesMaxLength);
    }
}

/// <summary>Boundary twin (§3.4) for <c>PUT /groups/{g}/expenses/{e}</c> — the full-aggregate replace body.</summary>
public sealed class ReplaceExpenseRequestValidator : AbstractValidator<ReplaceExpenseRequest>
{
    public ReplaceExpenseRequestValidator(IClock clock, IOptions<OperationalConstants> ops)
    {
        ExpenseBodyRules.Apply(this, clock, ops.Value.NotesMaxLength);
    }
}

/// <summary>
/// The shared expense-body twins (§3.4) applied to any <see cref="IExpenseBody"/>. Each rule carries the
/// specific catalog <c>code</c> so a single-cause failure surfaces its precise 422 twin
/// (<c>title_length</c>, <c>amount_not_positive</c>, <c>split_sum_mismatch</c>, …) via the endpoint filter.
/// Member-in-group / category existence are DB-scoped and enforced in the handler, not here.
/// </summary>
public static class ExpenseBodyRules
{
    private static readonly DateOnly MinDate = new(2000, 1, 1);
    private const int IconSymbolMaxLength = 32;

    public static void Apply<T>(AbstractValidator<T> v, IClock clock, int notesMax)
        where T : IExpenseBody
    {
        v.RuleFor(x => x.Title)
            .Must(t => !string.IsNullOrEmpty(t) && t!.Length <= 140)
            .WithErrorCode(ProblemCodes.TitleLength)
            .WithMessage("Title must be 1–140 characters.");

        v.RuleFor(x => x.Amount)
            .Must(ExpenseWire.IsCanonicalPositiveMinor)
            .WithErrorCode(ProblemCodes.AmountNotPositive)
            .WithMessage("Amount must be a positive integer of minor units (no leading zeros).");

        v.RuleFor(x => x.Currency)
            .Must(c => c is not null && ExpenseWire.AllowedCurrencies.Contains(c))
            .WithErrorCode(ProblemCodes.UnsupportedCurrency)
            .WithMessage("Currency must be one of CZK, EUR, USD, GBP.");

        v.RuleFor(x => x.PaidBy)
            .Must(BeGuid)
            .WithErrorCode(ProblemCodes.SplitMemberInvalid)
            .WithMessage("paidBy must reference a group member.");

        v.RuleFor(x => x.Date)
            .Must(d => IsDateInRange(d, clock))
            .WithErrorCode(ProblemCodes.DateOutOfRange)
            .WithMessage("Date must be a valid ISO date within the allowed range.");

        v.RuleFor(x => x.Notes)
            .Must(n => n is null || n.Length <= notesMax)
            .WithErrorCode(ProblemCodes.NotesTooLong)
            .WithMessage($"Notes must be at most {notesMax} characters.");

        v.RuleFor(x => x.IconSymbol)
            .Must(s => s is null || s.Length <= IconSymbolMaxLength)
            .WithMessage($"iconSymbol must be at most {IconSymbolMaxLength} characters.");

        v.RuleFor(x => x.CategoryId)
            .Must(c => c is null || BeGuid(c))
            .WithMessage("categoryId must be a valid id.");

        v.RuleFor(x => x.Split)
            .NotNull()
            .WithErrorCode(ProblemCodes.InvalidSplitType)
            .WithMessage("A split is required.");

        v.RuleFor(x => x).Custom((body, ctx) => ValidateSplit(body, ctx));
    }

    private static void ValidateSplit<T>(T body, ValidationContext<T> ctx)
        where T : IExpenseBody
    {
        var split = body.Split;
        if (split is null)
            return; // the NotNull rule already flagged invalid_split_type

        switch (split.Type)
        {
            case SplitKind.Equal:
                ValidateMembers(split.Among, ctx);
                break;

            case SplitKind.Exact:
            {
                var entries = split.Amounts;
                if (entries is null || entries.Count == 0)
                {
                    AddSplitMemberInvalid(ctx, "EXACT split needs at least one member.");
                    return;
                }
                if (!ValidateMembers(entries.Select(e => e.MemberId).ToList(), ctx))
                    return;
                foreach (var e in entries)
                {
                    if (!ExpenseWire.IsCanonicalNonNegativeMinor(e.Amount))
                    {
                        AddFailure(ctx, ProblemCodes.ShareNegative,
                            "EXACT amounts must be non-negative integer minor units.");
                        return;
                    }
                }
                if (ExpenseWire.IsCanonicalPositiveMinor(body.Amount))
                {
                    var total = ExpenseWire.ParseMinor(body.Amount!);
                    long sum = 0;
                    foreach (var e in entries)
                        sum += ExpenseWire.ParseMinor(e.Amount);
                    if (sum != total)
                        AddFailure(ctx, ProblemCodes.SplitSumMismatch,
                            "EXACT amounts must sum to the expense amount.");
                }
                break;
            }

            case SplitKind.Shares:
            {
                var entries = split.Weights;
                if (entries is null || entries.Count == 0)
                {
                    AddSplitMemberInvalid(ctx, "SHARES split needs at least one member.");
                    return;
                }
                if (!ValidateMembers(entries.Select(e => e.MemberId).ToList(), ctx))
                    return;
                foreach (var e in entries)
                {
                    if (e.Weight < 1)
                    {
                        AddFailure(ctx, ProblemCodes.InvalidWeight, "Each SHARES weight must be an integer >= 1.");
                        return;
                    }
                }
                break;
            }

            case SplitKind.Percentage:
            {
                var entries = split.Percents;
                if (entries is null || entries.Count == 0)
                {
                    AddSplitMemberInvalid(ctx, "PERCENTAGE split needs at least one member.");
                    return;
                }
                if (!ValidateMembers(entries.Select(e => e.MemberId).ToList(), ctx))
                    return;
                foreach (var e in entries)
                {
                    if (e.BasisPoints < 1)
                    {
                        AddFailure(ctx, ProblemCodes.InvalidBasisPoints, "Each basisPoints must be an integer >= 1.");
                        return;
                    }
                }
                var sumBp = entries.Sum(e => (long)e.BasisPoints);
                if (sumBp != 10_000)
                    AddFailure(ctx, ProblemCodes.SplitPercentSumMismatch,
                        "Percentage basisPoints must sum to 10000.");
                break;
            }

            default:
                AddFailure(ctx, ProblemCodes.InvalidSplitType, "Unknown split type.");
                break;
        }
    }

    private static bool ValidateMembers<T>(IReadOnlyList<string>? ids, ValidationContext<T> ctx)
    {
        if (ids is null || ids.Count == 0)
        {
            AddSplitMemberInvalid(ctx, "Split needs at least one member.");
            return false;
        }
        var seen = new HashSet<Guid>();
        foreach (var raw in ids)
        {
            if (!Guid.TryParse(raw, out var g))
            {
                AddSplitMemberInvalid(ctx, "Split members must be valid group member ids.");
                return false;
            }
            if (!seen.Add(g))
            {
                AddSplitMemberInvalid(ctx, "Split members must be unique.");
                return false;
            }
        }
        return true;
    }

    private static void AddSplitMemberInvalid<T>(ValidationContext<T> ctx, string message) =>
        AddFailure(ctx, ProblemCodes.SplitMemberInvalid, message);

    private static void AddFailure<T>(ValidationContext<T> ctx, string code, string message) =>
        ctx.AddFailure(new ValidationFailure("split", message) { ErrorCode = code });

    private static bool BeGuid(string? s) => Guid.TryParse(s, out _);

    private static bool IsDateInRange(string? raw, IClock clock)
    {
        if (!ExpenseWire.TryParseDate(raw, out var date))
            return false;
        // Server twin bound: local_today+1 ≤ utc_today+2 (the immutable-safe CHECK upper bound, §3.4).
        var max = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddDays(1);
        return date >= MinDate && date <= max;
    }
}

using FluentValidation;
using Microsoft.Extensions.Options;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Common.Options;
using Paybitch.Api.Features.Expenses;

namespace Paybitch.Api.Features.Recurring;

/// <summary>Boundary twin (§3.4) for <c>POST /groups/{g}/recurring-rules</c>.</summary>
public sealed class CreateRecurringRuleRequestValidator : AbstractValidator<CreateRecurringRuleRequest>
{
    public CreateRecurringRuleRequestValidator(IOptions<OperationalConstants> ops)
    {
        RuleFor(x => x.ClientId)
            .Must(c => !string.IsNullOrWhiteSpace(c) && c!.Length <= 200)
            .WithMessage("clientId is required and must be at most 200 characters.");

        RecurringBodyRules.Apply(this, ops.Value.NotesMaxLength);
    }
}

/// <summary>Boundary twin (§3.4) for <c>PUT /groups/{g}/recurring-rules/{r}</c> — the full-aggregate replace body.</summary>
public sealed class ReplaceRecurringRuleRequestValidator : AbstractValidator<ReplaceRecurringRuleRequest>
{
    public ReplaceRecurringRuleRequestValidator(IOptions<OperationalConstants> ops)
    {
        RecurringBodyRules.Apply(this, ops.Value.NotesMaxLength);
    }
}

/// <summary>
/// The shared scalar twins (§3.4) that carry existing catalog codes. The cross-field recurrence-shape,
/// timezone, end-bound-xor and split-template semantics own their own <c>invalid_recurrence</c> /
/// <c>invalid_timezone</c> / <c>recurrence_end_ambiguous</c> / <c>recurring_split_required</c> codes and are
/// enforced in the handler (<see cref="RecurringSemantics"/>) so the wire <c>code</c> is exact.
/// </summary>
public static class RecurringBodyRules
{
    private const int IconSymbolMaxLength = 32;

    public static void Apply<T>(AbstractValidator<T> v, int notesMax)
        where T : IRecurringRuleBody
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
            .Must(p => Guid.TryParse(p, out _))
            .WithErrorCode(ProblemCodes.SplitMemberInvalid)
            .WithMessage("paidBy must reference a group member.");

        v.RuleFor(x => x.Notes)
            .Must(n => n is null || n.Length <= notesMax)
            .WithErrorCode(ProblemCodes.NotesTooLong)
            .WithMessage($"Notes must be at most {notesMax} characters.");

        v.RuleFor(x => x.IconSymbol)
            .Must(s => s is null || s.Length <= IconSymbolMaxLength)
            .WithMessage($"iconSymbol must be at most {IconSymbolMaxLength} characters.");

        v.RuleFor(x => x.CategoryId)
            .Must(c => c is null || Guid.TryParse(c, out _))
            .WithMessage("categoryId must be a valid id.");

        v.RuleFor(x => x.Recurrence)
            .NotNull()
            .WithMessage("A recurrence is required.");

        v.RuleFor(x => x.Split)
            .NotNull()
            .WithErrorCode(ProblemCodes.InvalidSplitType)
            .WithMessage("A split is required.");
    }
}

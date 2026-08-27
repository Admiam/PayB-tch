using System.Globalization;
using System.Text.RegularExpressions;
using FluentValidation;
using Paybitch.Api.Common.Errors;

namespace Paybitch.Api.Features.Settlements;

/// <summary>
/// Boundary validation for <see cref="CreateSettlementRequest"/> — the 422 twins of the
/// <c>settlements</c> CHECK constraints (§3.4 twin table). Per-rule catalog codes drive the top-level
/// problem <c>code</c> when exactly one twin fires; shape failures fall back to <c>validation_failed</c>.
/// </summary>
public sealed class CreateSettlementValidator : AbstractValidator<CreateSettlementRequest>
{
    // Canonical positive integer: no sign, no leading zero, no "0" (rejects "0084" too, §3.4).
    private const string CanonicalPositiveInteger = "^[1-9][0-9]*$";

    private static readonly IReadOnlySet<string> SupportedCurrencies =
        new HashSet<string>(StringComparer.Ordinal) { "CZK", "EUR", "USD", "GBP" };

    // No settled_on CHECK exists in the schema; this lower bound only rejects an omitted/garbage date.
    private static readonly DateOnly MinSettledOn = new(2000, 1, 1);

    public CreateSettlementValidator()
    {
        RuleFor(x => x.ClientId)
            .NotEmpty()
            .MaximumLength(128);

        RuleFor(x => x.FromMember)
            .NotEqual(Guid.Empty);

        RuleFor(x => x.ToMember)
            .NotEqual(Guid.Empty);

        // from_member <> to_member (§3.4 twin). Anchored to toMember so the error carries a field.
        RuleFor(x => x.ToMember)
            .Must((req, to) => req.FromMember != to)
            .WithErrorCode(ProblemCodes.SelfSettlement)
            .WithMessage("A settlement cannot be from and to the same member.");

        // amount_minor > 0 twin — a single canonical positive minor-unit check so the twin code
        // always surfaces (a leading-zero / non-numeric / "0" string all resolve to amount_not_positive).
        RuleFor(x => x.Amount)
            .Must(BeCanonicalPositiveMinor)
            .WithErrorCode(ProblemCodes.AmountNotPositive)
            .WithMessage("Amount must be a positive integer of minor units.");

        // currency ∈ closed v1 set (FK backstop) twin.
        RuleFor(x => x.Currency)
            .Must(c => c is not null && SupportedCurrencies.Contains(c))
            .WithErrorCode(ProblemCodes.UnsupportedCurrency)
            .WithMessage("Currency is not supported in v1.");

        RuleFor(x => x.SettledOn)
            .GreaterThanOrEqualTo(MinSettledOn)
            .WithMessage("settledOn is required and must be a valid date.");

        // notes <= 2000 twin.
        RuleFor(x => x.Notes)
            .MaximumLength(2000)
            .WithErrorCode(ProblemCodes.NotesTooLong)
            .WithMessage("Notes exceed the maximum length.")
            .When(x => x.Notes is not null);

        RuleFor(x => x.Method)
            .MaximumLength(50)
            .When(x => x.Method is not null);
    }

    private static bool BeCanonicalPositiveMinor(string? amount) =>
        !string.IsNullOrEmpty(amount)
        && Regex.IsMatch(amount, CanonicalPositiveInteger)
        && long.TryParse(amount, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value > 0;
}

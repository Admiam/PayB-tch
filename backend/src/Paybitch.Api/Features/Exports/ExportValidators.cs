using FluentValidation;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Features.Expenses;

namespace Paybitch.Api.Features.Exports;

/// <summary>
/// Boundary twin (§3.4) for <c>POST /groups/{g}/exports</c>. <c>kind</c> is the async group allowlist
/// (<c>csv</c>/<c>pdf</c> — GDPR has its own <c>POST /me/export</c> route); optional <c>from</c>/<c>to</c>
/// must be strict ISO dates with <c>from ≤ to</c>. Auto-registered by the API assembly scan.
/// </summary>
public sealed class CreateExportRequestValidator : AbstractValidator<CreateExportRequest>
{
    public CreateExportRequestValidator()
    {
        RuleFor(x => x.Kind)
            .Must(k => k is not null && ExportKinds.AsyncGroupKinds.Contains(k))
            .WithErrorCode(ProblemCodes.ValidationFailed)
            .WithMessage("kind must be one of: csv, pdf.");

        RuleFor(x => x.From)
            .Must(BeNullOrIsoDate)
            .WithErrorCode(ProblemCodes.DateOutOfRange)
            .WithMessage("from must be an ISO date (yyyy-MM-dd).");

        RuleFor(x => x.To)
            .Must(BeNullOrIsoDate)
            .WithErrorCode(ProblemCodes.DateOutOfRange)
            .WithMessage("to must be an ISO date (yyyy-MM-dd).");

        RuleFor(x => x)
            .Must(HaveOrderedRange)
            .WithErrorCode(ProblemCodes.DateOutOfRange)
            .WithMessage("from must be on or before to.")
            .WithName("to");
    }

    private static bool BeNullOrIsoDate(string? raw) =>
        raw is null || ExpenseWire.TryParseDate(raw, out _);

    private static bool HaveOrderedRange(CreateExportRequest r)
    {
        if (!ExpenseWire.TryParseDate(r.From, out var from) || !ExpenseWire.TryParseDate(r.To, out var to))
            return true; // only compare when BOTH parse; single-field errors are surfaced above
        return from <= to;
    }
}

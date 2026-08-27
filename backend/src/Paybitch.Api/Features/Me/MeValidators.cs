using FluentValidation;

namespace Paybitch.Api.Features.Me;

/// <summary>
/// <c>PATCH /me</c> shape validation. All fields are optional (partial update), but a provided
/// <c>displayName</c> must be non-empty and bounded, and <c>locale</c> bounded. Currency support is
/// checked in the handler against the closed D2 set (→ <c>422 unsupported_currency</c>), and the
/// image-URL over-post traps are rejected in the handler (→ <c>422 immutable_field</c>).
/// </summary>
public sealed class UpdateMeRequestValidator : AbstractValidator<UpdateMeRequest>
{
    private const int DisplayNameMax = 100;
    private const int LocaleMax = 35; // BCP-47 tags stay well under this

    public UpdateMeRequestValidator()
    {
        When(x => x.DisplayName is not null, () =>
            RuleFor(x => x.DisplayName)
                .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("displayName must not be empty.")
                .MaximumLength(DisplayNameMax));

        When(x => x.Locale is not null, () =>
            RuleFor(x => x.Locale)
                .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("locale must not be empty.")
                .MaximumLength(LocaleMax));
    }
}

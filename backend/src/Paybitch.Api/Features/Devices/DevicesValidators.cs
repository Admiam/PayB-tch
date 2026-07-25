using FluentValidation;

namespace Paybitch.Api.Features.Devices;

/// <summary>
/// <c>PUT /me/devices/{id}</c> shape validation. <c>apnsToken</c> is mandatory; <c>kind</c>, when
/// present, must match the DB check (<c>apns</c> | <c>apns_live_activity</c>).
/// </summary>
public sealed class RegisterDeviceRequestValidator : AbstractValidator<RegisterDeviceRequest>
{
    private static readonly string[] Kinds = ["apns", "apns_live_activity"];

    public RegisterDeviceRequestValidator()
    {
        RuleFor(x => x.ApnsToken).NotEmpty();

        When(x => x.Kind is not null, () =>
            RuleFor(x => x.Kind)
                .Must(k => Kinds.Contains(k))
                .WithMessage("kind must be 'apns' or 'apns_live_activity'."));

        When(x => x.Platform is not null, () =>
            RuleFor(x => x.Platform).MaximumLength(32));
    }
}

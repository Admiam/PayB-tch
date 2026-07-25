using FluentValidation;

namespace Paybitch.Api.Features.EmailAuth;

/// <summary>
/// Shape validation for the E6 requests (§4.3). A missing/invalid field short-circuits to
/// <c>422 validation_failed</c> before any token or DB work — this is a SHAPE gate (format), not an
/// existence probe, so it never conflicts with the enumeration-safe uniform <c>200</c> of <c>…/start</c>
/// (which is about whether an address EXISTS, not whether it is well-formed).
/// </summary>
public sealed class EmailStartRequestValidator : AbstractValidator<EmailStartRequest>
{
    public EmailStartRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
    }
}

public sealed class EmailVerifyRequestValidator : AbstractValidator<EmailVerifyRequest>
{
    public EmailVerifyRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Code).NotEmpty().Matches("^[0-9]{6}$");
    }
}

public sealed class LinkStartRequestValidator : AbstractValidator<LinkStartRequest>
{
    public LinkStartRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
    }
}

public sealed class LinkVerifyRequestValidator : AbstractValidator<LinkVerifyRequest>
{
    public LinkVerifyRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Matches("^[0-9]{6}$");
    }
}

public sealed class EmailChangeStartRequestValidator : AbstractValidator<EmailChangeStartRequest>
{
    public EmailChangeStartRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
    }
}

public sealed class EmailChangeVerifyRequestValidator : AbstractValidator<EmailChangeVerifyRequest>
{
    public EmailChangeVerifyRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Matches("^[0-9]{6}$");
    }
}

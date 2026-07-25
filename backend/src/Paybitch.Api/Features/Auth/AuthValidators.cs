using FluentValidation;

namespace Paybitch.Api.Features.Auth;

/// <summary>
/// <c>POST /auth/apple</c> shape validation (§4.1). <c>identityToken</c>, <c>authorizationCode</c>
/// (required — it is the sole route to Apple's revoke material, §4.4 step 9) and <c>nonce</c> are all
/// mandatory; a missing field short-circuits to <c>422 validation_failed</c> before any Apple call.
/// </summary>
public sealed class AppleSignInRequestValidator : AbstractValidator<AppleSignInRequest>
{
    public AppleSignInRequestValidator()
    {
        RuleFor(x => x.IdentityToken).NotEmpty();
        RuleFor(x => x.AuthorizationCode).NotEmpty();
        RuleFor(x => x.Nonce).NotEmpty();
    }
}

/// <summary><c>POST /auth/refresh</c> — the refresh token must be present.</summary>
public sealed class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty();
    }
}

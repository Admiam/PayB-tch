using FluentValidation;

namespace Paybitch.Api.Features.Comments;

/// <summary>
/// Boundary twin (§3.4) for <c>POST /groups/{g}/expenses/{e}/comments</c>. Only <c>clientId</c> is a
/// structural precondition here (resolves to the generic <c>validation_failed</c>); the
/// <c>comment_empty</c> / <c>comment_too_long</c> body twins carry feature-local codes not yet in the
/// shared catalog, so they are enforced in the handler (<see cref="CommentMapping.ValidateBody"/>) to
/// keep the wire <c>code</c> exact — mirrors how <c>RecurringSemantics</c> emits its custom codes.
/// </summary>
public sealed class CreateCommentRequestValidator : AbstractValidator<CreateCommentRequest>
{
    public CreateCommentRequestValidator()
    {
        RuleFor(x => x.ClientId)
            .Must(c => !string.IsNullOrWhiteSpace(c) && c!.Length <= 200)
            .WithMessage("clientId is required and must be at most 200 characters.");
    }
}

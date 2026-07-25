using Microsoft.AspNetCore.Http;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Features.Expenses;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Comments;

/// <summary>
/// Wire mapping for a comment: the canonical <see cref="CommentResponse"/> (reused by REST and, on
/// convergence, by <c>/sync</c> hydration) plus the body twin guard. Author display name is resolved from
/// the caller's <c>group_members</c> row at read time (EXT-DC1), never stored on the comment.
/// </summary>
public static class CommentMapping
{
    /// <summary>Body bound: <c>1 ≤ length ≤ 2000</c> (§7.3 CHECK; the 422 twins <c>comment_empty</c> / <c>comment_too_long</c>).</summary>
    public const int MaxBodyLength = 2000;

    /// <summary>
    /// The body twin guard (§7.4). Returns the exact 422 problem for an empty or over-long body, or
    /// <c>null</c> when the body is in range. Enforced in the handler (not the FluentValidation filter) so
    /// the wire <c>code</c> is the feature-local twin rather than the generic <c>validation_failed</c>.
    /// </summary>
    public static IResult? ValidateBody(string? body)
    {
        if (string.IsNullOrEmpty(body))
            return Problems.Validation(CommentProblemCodes.CommentEmpty, "body", "Comment body must not be empty.");
        if (body.Length > MaxBodyLength)
            return Problems.Validation(
                CommentProblemCodes.CommentTooLong, "body", $"Comment body must be at most {MaxBodyLength} characters.");
        return null;
    }

    /// <summary>The canonical representation (§7.4). <paramref name="author"/> is the resolved member row, or null when unresolved.</summary>
    public static CommentResponse ToResponse(Comment c, GroupMember? author) =>
        new(
            Id: c.Id.ToString(),
            ClientId: c.ClientId,
            GroupId: c.GroupId.ToString(),
            ExpenseId: c.ExpenseId.ToString(),
            Author: ToAuthor(c.AuthorUser, author),
            Body: c.Body,
            CreatedAt: ExpenseWire.Timestamp(c.CreatedAt),
            UpdatedAt: ExpenseWire.Timestamp(c.UpdatedAt),
            Edited: c.Version > 1,
            Version: c.Version,
            Deleted: c.DeletedAt is not null);

    /// <summary>Author block hydrated via the member row (EXT-DC1); memberId/displayName are null when no row resolves.</summary>
    public static CommentAuthor ToAuthor(Guid authorUser, GroupMember? member) =>
        new(authorUser.ToString(), member?.Id.ToString(), member?.DisplayName);
}

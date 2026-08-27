using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Api.Common.Validation;

namespace Paybitch.Api.Features.Comments;

/// <summary>
/// Self-registers the E10a comment routes (§7.4). The thread list/create hang off the expense
/// (<c>/groups/{groupId}/expenses/{expenseId}/comments</c>); edit/delete address a comment by id
/// (<c>/groups/{groupId}/comments/{commentId}</c>). Every surface is member-scoped (D6): the group-level
/// <c>.RequireGroupMembership()</c> runs first (404 for non-members), before body validation and the
/// handler's archived / idempotency / If-Match / author checks. DELETE stays member-scoped (not
/// <c>.RequireGroupAdmin()</c>) because a plain-member author may delete their own comment — the
/// author-or-admin decision is made in the handler.
/// </summary>
public sealed class CommentsModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var thread = app
            .MapGroup("/groups/{groupId}/expenses/{expenseId}/comments")
            .RequireGroupMembership()
            .WithTags("Comments");

        thread.MapGet("", CommentEndpoints.ListAsync)
            .WithName("ListComments")
            .WithSummary("List an expense's comment thread, oldest first (cursor envelope, §7.4).");

        thread.MapPost("", CommentEndpoints.CreateAsync)
            .WithValidation()
            .WithName("CreateComment")
            .WithSummary("Add a comment to an expense; author is the caller (§7.4, EXT-DC7).");

        var byId = app
            .MapGroup("/groups/{groupId}/comments")
            .RequireGroupMembership()
            .WithTags("Comments");

        byId.MapPatch("/{commentId}", CommentEndpoints.PatchAsync)
            .WithName("EditComment")
            .WithSummary("Edit a comment — author only, If-Match required (§7.4, EXT-DC5).");

        byId.MapDelete("/{commentId}", CommentEndpoints.DeleteAsync)
            .WithName("DeleteComment")
            .WithSummary("Soft-delete a comment — author or admin, If-Match required (§7.4, EXT-DC5).");
    }
}

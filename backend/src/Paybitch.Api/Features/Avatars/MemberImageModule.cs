using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Features.Groups.Support;
using Paybitch.Api.Features.Platform.Blob;
using Paybitch.Api.Features.Platform.Jobs;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Avatars;

/// <summary>
/// E7 — the member image (§1.4). Rides the reserved <c>group_members.image_url</c> column and the
/// existing <c>member</c> sync entity (EXT-D1l — no new sync type; a set/clear is a <c>member</c> upsert
/// with a version bump and a <c>change_log</c> row in the mutation's transaction). AuthZ extends the §3.1
/// self-only rule additively (EXT-D1o): you set your OWN member image; an admin/owner sets a GHOST's
/// (an unlinked member that cannot authenticate to set its own); setting another LINKED member's image is
/// <c>403 insufficient_role</c>. Any member may READ any member's image; delete is self-or-admin.
/// </summary>
public sealed class MemberImageModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        const string Route = "/groups/{groupId}/members/{memberId}/image";

        app.MapPost(Route, UploadAsync)
            .RequireGroupMembership()
            .RequireGroupNotArchived()
            .WithName("SetMemberImage")
            .WithSummary("Upload (multipart, ≤2MB) a member image (self, or a ghost's by an admin).")
            .WithTags("Avatars");

        app.MapGet(Route, GetAsync)
            .RequireGroupMembership()
            .WithName("GetMemberImage")
            .WithSummary("Fetch a member image (any member; 302 to a presigned GET or streamed bytes).")
            .WithTags("Avatars");

        app.MapDelete(Route, DeleteAsync)
            .RequireGroupMembership()
            .RequireGroupNotArchived()
            .WithName("DeleteMemberImage")
            .WithSummary("Remove a member image (self or admin; null column + two-phase blob delete).")
            .WithTags("Avatars");
    }

    private static async Task<IResult> UploadAsync(
        Guid memberId, HttpContext http, AppDbContext db, IBlobStore blobStore, IJobQueue jobs,
        IChangeLogWriter changeLog, CancellationToken ct)
    {
        var membership = http.GetMembership();

        var upload = await AvatarBlobs.ReadUploadAsync(http, ct);
        if (!upload.Ok)
            return upload.Error!;

        var member = await db.GroupMembers
            .FirstOrDefaultAsync(m => m.Id == memberId && m.GroupId == membership.GroupId && m.DeletedAt == null, ct);
        if (member is null)
            return Problems.NotFound();

        if (!CanWrite(member, membership))
            return Problems.Forbidden(ProblemCodes.InsufficientRole);

        var processed = await AvatarPipeline.ProcessAsync(upload.Bytes!, ct);
        if (!processed.Ok)
            return processed.Error!;

        string newKey;
        using (var jpeg = processed.Jpeg!)
            newKey = await AvatarBlobs.StoreAsync(blobStore, jpeg, ct);

        var previousKey = member.ImageUrl;
        member.ImageUrl = newKey;
        member.Version += 1;
        changeLog.Append(membership.GroupId, ChangeLogEntityTypes.Member, member.Id, isDelete: false);

        if (!await AvatarBlobs.EnqueueDeleteAsync(db, jobs, previousKey, AvatarBlobs.ReasonReplaced, ct))
            await db.SaveChangesAsync(ct);

        ConcurrencyHeaders.SetETag(http.Response, member.Version);
        return Results.Ok(new ImageSetResponse(
            $"/v1/groups/{membership.GroupId}/members/{member.Id}/image", member.Version));
    }

    private static async Task<IResult> GetAsync(
        Guid memberId, HttpContext http, AppDbContext db, IBlobStore blobStore, CancellationToken ct)
    {
        var membership = http.GetMembership();

        var key = await db.GroupMembers
            .Where(m => m.Id == memberId && m.GroupId == membership.GroupId && m.DeletedAt == null)
            .Select(m => m.ImageUrl)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(key))
            return Problems.NotFound();

        return await AvatarBlobs.ServeAsync(http, blobStore, key, ct);
    }

    private static async Task<IResult> DeleteAsync(
        Guid memberId, HttpContext http, AppDbContext db, IJobQueue jobs,
        IChangeLogWriter changeLog, CancellationToken ct)
    {
        var membership = http.GetMembership();

        var member = await db.GroupMembers
            .FirstOrDefaultAsync(m => m.Id == memberId && m.GroupId == membership.GroupId && m.DeletedAt == null, ct);
        if (member is null)
            return Problems.NotFound();

        // Delete is self-or-admin (EXT-D1o mirrors §3.6 void rights).
        var isSelf = member.Id == membership.MemberId;
        if (!isSelf && !MembershipOps.IsPrivileged(membership.Role))
            return Problems.Forbidden(ProblemCodes.InsufficientRole);

        var previousKey = member.ImageUrl;
        if (previousKey is null)
            return Results.NoContent(); // nothing to clear — idempotent

        member.ImageUrl = null;
        member.Version += 1;
        changeLog.Append(membership.GroupId, ChangeLogEntityTypes.Member, member.Id, isDelete: false);

        if (!await AvatarBlobs.EnqueueDeleteAsync(db, jobs, previousKey, AvatarBlobs.ReasonReplaced, ct))
            await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    /// <summary>
    /// EXT-D1o write rule: self always; an admin/owner only for an unclaimed GHOST (<c>user_id IS NULL</c>);
    /// any other linked member is forbidden.
    /// </summary>
    private static bool CanWrite(GroupMember member, MembershipInfo membership)
    {
        if (member.Id == membership.MemberId)
            return true;
        return member.UserId is null && MembershipOps.IsPrivileged(membership.Role);
    }
}

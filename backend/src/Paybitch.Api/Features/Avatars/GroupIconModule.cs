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

namespace Paybitch.Api.Features.Avatars;

/// <summary>
/// E7 — the group icon image (§1.4). Rides the added <c>groups.image_url</c> column and the existing
/// <c>group</c> sync entity (EXT-D1l): a set/clear is a <c>group</c> upsert with a version bump and a
/// <c>change_log</c> row in the mutation's transaction. Set/clear are admin/owner only (EXT-D1o); any
/// member may read. The image is group content (not the D7 PII class), so it is NOT hard-deleted on a
/// member anonymize — only on an explicit clear or replace.
/// </summary>
public sealed class GroupIconModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        const string Route = "/groups/{groupId}/icon";

        app.MapPost(Route, UploadAsync)
            .RequireGroupAdmin()
            .RequireGroupNotArchived()
            .WithName("SetGroupIcon")
            .WithSummary("Upload (multipart, ≤2MB) and set the group icon image (admin).")
            .WithTags("Avatars");

        app.MapGet(Route, GetAsync)
            .RequireGroupMembership()
            .WithName("GetGroupIcon")
            .WithSummary("Fetch the group icon image (any member; 302 to a presigned GET or streamed bytes).")
            .WithTags("Avatars");

        app.MapDelete(Route, DeleteAsync)
            .RequireGroupAdmin()
            .RequireGroupNotArchived()
            .WithName("DeleteGroupIcon")
            .WithSummary("Remove the group icon image (admin; null column + two-phase blob delete).")
            .WithTags("Avatars");
    }

    private static async Task<IResult> UploadAsync(
        HttpContext http, AppDbContext db, IBlobStore blobStore, IJobQueue jobs,
        IChangeLogWriter changeLog, CancellationToken ct)
    {
        var membership = http.GetMembership();

        var upload = await AvatarBlobs.ReadUploadAsync(http, ct);
        if (!upload.Ok)
            return upload.Error!;

        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == membership.GroupId && g.DeletedAt == null, ct);
        if (group is null)
            return Problems.NotFound();

        var processed = await AvatarPipeline.ProcessAsync(upload.Bytes!, ct);
        if (!processed.Ok)
            return processed.Error!;

        string newKey;
        using (var jpeg = processed.Jpeg!)
            newKey = await AvatarBlobs.StoreAsync(blobStore, jpeg, ct);

        var previousKey = group.ImageUrl;
        group.ImageUrl = newKey;
        group.Version += 1;
        changeLog.Append(group.Id, ChangeLogEntityTypes.Group, group.Id, isDelete: false);

        if (!await AvatarBlobs.EnqueueDeleteAsync(db, jobs, previousKey, AvatarBlobs.ReasonReplaced, ct))
            await db.SaveChangesAsync(ct);

        ConcurrencyHeaders.SetETag(http.Response, group.Version);
        return Results.Ok(new ImageSetResponse($"/v1/groups/{group.Id}/icon", group.Version));
    }

    private static async Task<IResult> GetAsync(
        HttpContext http, AppDbContext db, IBlobStore blobStore, CancellationToken ct)
    {
        var membership = http.GetMembership();

        var key = await db.Groups
            .Where(g => g.Id == membership.GroupId && g.DeletedAt == null)
            .Select(g => g.ImageUrl)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(key))
            return Problems.NotFound();

        return await AvatarBlobs.ServeAsync(http, blobStore, key, ct);
    }

    private static async Task<IResult> DeleteAsync(
        HttpContext http, AppDbContext db, IJobQueue jobs, IChangeLogWriter changeLog, CancellationToken ct)
    {
        var membership = http.GetMembership();

        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == membership.GroupId && g.DeletedAt == null, ct);
        if (group is null)
            return Problems.NotFound();

        var previousKey = group.ImageUrl;
        if (previousKey is null)
            return Results.NoContent(); // nothing to clear — idempotent

        group.ImageUrl = null;
        group.Version += 1;
        changeLog.Append(group.Id, ChangeLogEntityTypes.Group, group.Id, isDelete: false);

        if (!await AvatarBlobs.EnqueueDeleteAsync(db, jobs, previousKey, AvatarBlobs.ReasonReplaced, ct))
            await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Features.Platform.Blob;
using Paybitch.Api.Features.Platform.Jobs;
using Paybitch.Infrastructure;

namespace Paybitch.Api.Features.Avatars;

/// <summary>
/// E7 — the user avatar (§1.4). A <c>/me</c>-only singleton on the reserved <c>users.avatar_url</c> column
/// (EXT-D1l): the upload passes through the <see cref="AvatarPipeline"/> security boundary, the canonical
/// JPEG is stored under a server-minted versioned key, and the previous version is two-phase hard-deleted.
/// Not group-scoped ⇒ no <c>change_log</c> row. As PII (a face), it is hard-deleted on delete and on the
/// §4.4 anonymize (the anonymize enqueue is owned by the auth/GDPR cluster — see the convergence note).
/// </summary>
public sealed class MeAvatarModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapPost("/me/avatar", UploadAsync)
            .WithName("SetMyAvatar")
            .WithSummary("Upload (multipart, ≤2MB) and set the authenticated user's avatar.")
            .WithTags("Avatars");

        app.MapGet("/me/avatar", GetAsync)
            .WithName("GetMyAvatar")
            .WithSummary("Fetch the authenticated user's avatar (302 to a presigned GET, or streamed bytes).")
            .WithTags("Avatars");

        app.MapDelete("/me/avatar", DeleteAsync)
            .WithName("DeleteMyAvatar")
            .WithSummary("Remove the authenticated user's avatar (null column + two-phase blob delete).")
            .WithTags("Avatars");
    }

    private static async Task<IResult> UploadAsync(
        HttpContext http, AppDbContext db, IBlobStore blobStore, IJobQueue jobs, ICurrentUser currentUser,
        CancellationToken ct)
    {
        var upload = await AvatarBlobs.ReadUploadAsync(http, ct);
        if (!upload.Ok)
            return upload.Error!;

        var processed = await AvatarPipeline.ProcessAsync(upload.Bytes!, ct);
        if (!processed.Ok)
            return processed.Error!;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == currentUser.UserId && u.DeletedAt == null, ct);
        if (user is null)
            return Problems.NotFound();

        string newKey;
        using (var jpeg = processed.Jpeg!)
            newKey = await AvatarBlobs.StoreAsync(blobStore, jpeg, ct);

        var previousKey = user.AvatarUrl;
        user.AvatarUrl = newKey;

        if (!await AvatarBlobs.EnqueueDeleteAsync(db, jobs, previousKey, AvatarBlobs.ReasonReplaced, ct))
            await db.SaveChangesAsync(ct);

        return Results.Ok(new AvatarSetResponse("/v1/me/avatar"));
    }

    private static async Task<IResult> GetAsync(
        HttpContext http, AppDbContext db, IBlobStore blobStore, ICurrentUser currentUser, CancellationToken ct)
    {
        var key = await db.Users
            .Where(u => u.Id == currentUser.UserId && u.DeletedAt == null)
            .Select(u => u.AvatarUrl)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(key))
            return Problems.NotFound();

        return await AvatarBlobs.ServeAsync(http, blobStore, key, ct);
    }

    private static async Task<IResult> DeleteAsync(
        AppDbContext db, IJobQueue jobs, ICurrentUser currentUser, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == currentUser.UserId && u.DeletedAt == null, ct);
        if (user is null)
            return Problems.NotFound();

        var previousKey = user.AvatarUrl;
        if (previousKey is null)
            return Results.NoContent(); // already cleared — idempotent

        user.AvatarUrl = null;
        if (!await AvatarBlobs.EnqueueDeleteAsync(db, jobs, previousKey, AvatarBlobs.ReasonReplaced, ct))
            await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }
}

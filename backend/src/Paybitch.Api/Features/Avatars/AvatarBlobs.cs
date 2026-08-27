using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Features.Platform.Blob;
using Paybitch.Api.Features.Platform.Jobs;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Avatars;

/// <summary>
/// Blob-side plumbing shared by the three E7 image surfaces (user avatar, member image, group icon):
/// reading the bounded multipart upload, storing the canonical JPEG under a versioned storage key, the
/// two-phase hard-delete of a superseded key (EXT-D1h), and the authenticated read (302 → presigned GET
/// in production, byte-stream on the filesystem dev store where presign is unavailable).
/// </summary>
/// <remarks>
/// Storage-key shape (a deliberate, correctness-driven refinement of the EXT-D1l
/// <c>avatars/{ownerType}/{id}/{n}</c> sketch): the object key is a fresh <see cref="Guid"/> per version,
/// rendered via <c>ToString()</c> and stored verbatim in the reserved <c>*_url</c> column (EXT-D1m — a
/// server-minted opaque key, never a client URL). This is what makes the EXISTING E0 two-phase delete
/// work unchanged: <see cref="BlobDeletion"/> keys on a <c>uuid</c> PK and
/// <c>BlobHardDeleteHandler</c> reclaims the object at <c>storageKey.ToString()</c>, so the object key
/// MUST be that same Guid. A new version = a new Guid = the client cache-busts for free (EXT-D1l intent),
/// and the superseded Guid is enqueued for reclamation.
/// </remarks>
public static class AvatarBlobs
{
    /// <summary>Hard byte cap on an avatar upload (§1.7 — avatars ride the same ≤ limit surface).</summary>
    public const long MaxUploadBytes = 2 * 1024 * 1024; // 2 MB

    /// <summary>Slack over <see cref="MaxUploadBytes"/> for the multipart envelope (boundary + part headers).</summary>
    private const long MultipartOverheadBytes = 8 * 1024;

    /// <summary>TTL for a production presigned GET (minutes — EXT-D1g).</summary>
    private const int PresignTtlMinutes = 5;

    /// <summary>Browser cache lifetime for a streamed avatar; the ETag (the storage key) forces revalidation on change.</summary>
    private const int CacheMaxAgeSeconds = 60;

    // blob_deletions.reason — mirrors the ck_blob_deletions_reason CHECK.
    /// <summary>A superseded or explicitly-removed avatar/image key (replace or DELETE endpoint).</summary>
    public const string ReasonReplaced = "avatar_replaced";

    /// <summary>An avatar erased by the §4.4 D7 anonymize (PII hard-delete — EXT-D1n).</summary>
    public const string ReasonGdprErase = "gdpr_erase";

    /// <summary>A read upload: the decoded bytes on success, or a client-facing <see cref="Error"/>.</summary>
    public readonly record struct Upload(byte[]? Bytes, IResult? Error)
    {
        public bool Ok => Error is null;
        public static Upload Success(byte[] bytes) => new(bytes, null);
        public static Upload Fail(IResult error) => new(null, error);
    }

    /// <summary>
    /// Read the single multipart file part into memory, bounded by <see cref="MaxUploadBytes"/>. Raises
    /// the per-request Kestrel body cap first (the global §5 256 KB JSON cap would otherwise reject a
    /// 2 MB image before the handler runs), then maps an over-cap body to <c>413</c> and a missing/empty
    /// part or non-multipart request to <c>415</c>.
    /// </summary>
    public static async Task<Upload> ReadUploadAsync(HttpContext http, CancellationToken ct)
    {
        // Must run before the body is read, while the feature is still writable.
        var sizeFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
            sizeFeature.MaxRequestBodySize = MaxUploadBytes + MultipartOverheadBytes;

        if (!http.Request.HasFormContentType)
            return Upload.Fail(AvatarProblems.UnsupportedMediaType("Expected a multipart/form-data upload."));

        IFormCollection form;
        try
        {
            form = await http.Request.ReadFormAsync(ct);
        }
        catch (BadHttpRequestException)
        {
            // Kestrel aborted the read for exceeding the raised body cap.
            return Upload.Fail(AvatarProblems.BlobTooLarge("Upload exceeds the 2 MB limit."));
        }

        var file = form.Files.GetFile("file") ?? (form.Files.Count > 0 ? form.Files[0] : null);
        if (file is null || file.Length == 0)
            return Upload.Fail(AvatarProblems.UnsupportedMediaType("No image file part was supplied."));

        if (file.Length > MaxUploadBytes)
            return Upload.Fail(AvatarProblems.BlobTooLarge("Upload exceeds the 2 MB limit."));

        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream(capacity: (int)file.Length);
        await stream.CopyToAsync(buffer, ct);
        return Upload.Success(buffer.ToArray());
    }

    /// <summary>
    /// Store the canonical JPEG under a fresh versioned key and return that key (the value to write into
    /// the reserved <c>*_url</c> column). The object is PUT here — BEFORE the row is committed — so a
    /// commit failure leaves an unreferenced orphan (swept later) rather than a column pointing at a
    /// missing object.
    /// </summary>
    public static async Task<string> StoreAsync(IBlobStore blobStore, MemoryStream jpeg, CancellationToken ct)
    {
        var key = Guid.CreateVersion7();
        jpeg.Position = 0;
        await blobStore.PutAsync(key.ToString(), jpeg, "image/jpeg", ct);
        return key.ToString();
    }

    /// <summary>
    /// Two-phase hard-delete of a storage key (EXT-D1h): write the <c>blob_deletions</c> row in the
    /// caller's transaction and enqueue the E0 <c>blob.hard_delete</c> job. Reason-agnostic so it serves
    /// both the replace/DELETE endpoints (<see cref="ReasonReplaced"/>) and the §4.4 anonymize
    /// (<see cref="ReasonGdprErase"/>). The enqueue runs with NO dedupe key so it always reaches
    /// <c>SaveChanges</c>, flushing the caller's pending mutations (the nulled/rewritten column, the
    /// version bump, the change_log row) and the ledger row atomically. Returns <c>false</c> — a no-op —
    /// for a null/non-Guid key (never set), letting the caller do its own <c>SaveChanges</c>.
    /// </summary>
    public static async Task<bool> EnqueueDeleteAsync(
        AppDbContext db, IJobQueue jobs, string? previousKey, string reason, CancellationToken ct)
    {
        if (previousKey is null || !Guid.TryParse(previousKey, out var key))
            return false;

        // Idempotent: the same superseded key is only ever enqueued once, but guard the PK regardless.
        if (!await db.BlobDeletions.AnyAsync(b => b.StorageKey == key, ct))
            db.BlobDeletions.Add(new BlobDeletion { StorageKey = key, Reason = reason });

        await jobs.EnqueueAsync(JobKinds.BlobHardDelete, new { storageKey = key }, ct: ct);
        return true;
    }

    /// <summary>
    /// Serve an image referenced by <paramref name="storageKey"/>: a <c>302</c> to a short-TTL presigned
    /// GET when the backing store can presign (production S3), otherwise the bytes streamed from
    /// <see cref="IBlobStore.GetAsync"/> with <c>image/jpeg</c>, a private cache header, and an ETag (the
    /// key) so the browser revalidates and gets a fresh image the moment the key changes.
    /// </summary>
    public static async Task<IResult> ServeAsync(HttpContext http, IBlobStore blobStore, string storageKey, CancellationToken ct)
    {
        var presigned = blobStore.TryGetPresignedGetUrl(storageKey, TimeSpan.FromMinutes(PresignTtlMinutes));
        if (presigned is not null)
            return Results.Redirect(presigned.ToString(), permanent: false);

        var stream = await blobStore.GetAsync(storageKey, ct);
        if (stream is null)
            return Problems.NotFound(); // column references an object that is gone (e.g. reclaimed) — treat as absent

        http.Response.Headers.CacheControl = $"private, max-age={CacheMaxAgeSeconds}";
        var entityTag = new EntityTagHeaderValue($"\"{storageKey}\"");
        return Results.Stream(stream, contentType: "image/jpeg", entityTag: entityTag, enableRangeProcessing: false);
    }
}

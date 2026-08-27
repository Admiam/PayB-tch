namespace Paybitch.Api.Features.Platform.Blob;

/// <summary>
/// The E0 blob abstraction (§0.4.3). Object bytes live <b>outside</b> Postgres, referenced only by an
/// opaque <c>key</c> stored in a DB row (X4) — the interface never enumerates. The production target is
/// S3-compatible object storage (Hetzner Object Storage / Cloudflare R2) behind this same contract; the
/// members exposed here are the <b>intersection</b> both providers guarantee, so a provider swap can
/// never silently break (EXT-D0e). <see cref="FilesystemBlobStore"/> is the dev/default implementation.
/// </summary>
public interface IBlobStore
{
    /// <summary>Store <paramref name="content"/> at <paramref name="key"/>, overwriting any existing object (atomic).</summary>
    Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default);

    /// <summary>Open the object at <paramref name="key"/> for reading, or null when absent. Caller disposes the stream.</summary>
    Task<Stream?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Delete the object at <paramref name="key"/>. Idempotent: an absent object is success (X4).</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>True when an object exists at <paramref name="key"/> — the HEAD-style post-delete / post-upload verify.</summary>
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);

    /// <summary>The object's byte length, or null when absent.</summary>
    Task<long?> GetSizeAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// A short-TTL presigned GET URL, or null when the backend cannot presign (the filesystem dev store).
    /// A null return tells the caller to fall back to an authenticated stream endpoint (E4/E7 own those).
    /// </summary>
    Uri? TryGetPresignedGetUrl(string key, TimeSpan ttl);
}

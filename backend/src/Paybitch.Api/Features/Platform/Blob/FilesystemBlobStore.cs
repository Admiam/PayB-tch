namespace Paybitch.Api.Features.Platform.Blob;

/// <summary>
/// The dev/default <see cref="IBlobStore"/>: objects are files under a configured root directory
/// (<c>Blob:LocalRoot</c>, defaulting to a temp path). Writes are atomic (temp file + rename) and keys
/// are sanitized so a caller can never escape the root via path traversal. It cannot presign, so
/// <see cref="TryGetPresignedGetUrl"/> returns null (callers fall back to an authenticated endpoint).
/// </summary>
/// <remarks>
/// The production target behind <see cref="IBlobStore"/> is S3-compatible object storage (Hetzner Object
/// Storage / Cloudflare R2), where <see cref="TryGetPresignedGetUrl"/> returns a real short-TTL URL and
/// <paramref name="contentType"/> is persisted as object metadata. This filesystem store ignores
/// content type (the read path returns raw bytes only) and is not intended for production.
/// </remarks>
public sealed class FilesystemBlobStore : IBlobStore
{
    private const int CopyBufferSize = 81920;

    private readonly string _root;
    private readonly ILogger<FilesystemBlobStore> _logger;

    public FilesystemBlobStore(string? configuredRoot, ILogger<FilesystemBlobStore> logger)
    {
        _root = Path.GetFullPath(
            string.IsNullOrWhiteSpace(configuredRoot)
                ? Path.Combine(Path.GetTempPath(), "paybitch-blobs")
                : configuredRoot);
        _logger = logger;
        Directory.CreateDirectory(_root);
        _logger.LogInformation("FilesystemBlobStore rooted at {Root} (dev store — not for production)", _root);
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Atomic publish: stream into a sibling temp file, then rename over the target so a reader never
        // observes a partially written object.
        var temp = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var destination = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true))
            {
                await content.CopyToAsync(destination, ct);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDeleteTemp(temp);
            throw;
        }
    }

    public Task<Stream?> GetAsync(string key, CancellationToken ct = default)
    {
        var path = ResolvePath(key);
        if (!File.Exists(path))
            return Task.FromResult<Stream?>(null);

        Stream stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);
        return Task.FromResult<Stream?>(stream);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = ResolvePath(key);
        if (File.Exists(path))
            File.Delete(path);   // absent == success (X4): idempotent
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(File.Exists(ResolvePath(key)));

    public Task<long?> GetSizeAsync(string key, CancellationToken ct = default)
    {
        var path = ResolvePath(key);
        return Task.FromResult<long?>(File.Exists(path) ? new FileInfo(path).Length : null);
    }

    /// <summary>The filesystem store cannot presign — callers fall back to an authenticated stream endpoint.</summary>
    public Uri? TryGetPresignedGetUrl(string key, TimeSpan ttl) => null;

    /// <summary>
    /// Map an opaque blob key to an absolute path under the root, rejecting traversal. Segments may be
    /// separated by <c>/</c> (virtual folders); each is validated against illegal filename characters and
    /// the <c>.</c>/<c>..</c> relative segments, and the resolved path is asserted to stay under the root.
    /// </summary>
    private string ResolvePath(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Blob key must not be empty.", nameof(key));

        var segments = key.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            throw new ArgumentException("Blob key must contain at least one path segment.", nameof(key));

        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException($"Illegal blob key segment '{segment}'.", nameof(key));
        }

        var combined = Path.GetFullPath(Path.Combine([_root, .. segments]));
        var rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Blob key escapes the storage root.", nameof(key));

        return combined;
    }

    private void TryDeleteTemp(string temp)
    {
        try
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to clean up temp blob file {Temp}", temp);
        }
    }
}

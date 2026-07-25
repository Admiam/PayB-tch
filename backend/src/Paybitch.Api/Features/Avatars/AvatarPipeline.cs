using Microsoft.AspNetCore.Http;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace Paybitch.Api.Features.Avatars;

/// <summary>
/// The <b>image security boundary</b> (EXT-D1e, §1.7). Every uploaded avatar/member/icon image passes
/// through here before a single byte is stored: the header pixel dimensions are capped BEFORE a full
/// decode (decompression-bomb guard), the format is sniffed from the magic bytes (never the client's
/// declared content-type), the image is decoded with metadata skipped, all remaining EXIF/GPS/XMP/ICC
/// segments are dropped, it is cropped-resized to a canonical 512×512, and re-encoded as a fresh JPEG.
/// Re-encoding is the one operation that simultaneously kills EXIF, polyglot/GIFAR payloads, and any
/// bytes appended after the image data.
/// </summary>
public static class AvatarPipeline
{
    /// <summary>Canonical avatar edge length (EXT-D1e — avatars are 512×512).</summary>
    public const int TargetSize = 512;

    /// <summary>Output JPEG quality — visually lossless for a 512px avatar, small on the wire.</summary>
    public const int JpegQuality = 82;

    /// <summary>
    /// Decompression-bomb ceiling on the DECLARED canvas, checked from the header before any decode
    /// buffer is allocated. A 0.5 MB PNG that declares 100k×100k is rejected here, never decoded.
    /// </summary>
    public const long MaxInputPixels = 50_000_000; // 50 MP — comfortably above any legitimate phone photo

    /// <summary>Per-side dimension ceiling (a second bomb guard for extreme aspect ratios).</summary>
    public const int MaxInputDimension = 12_000;

    /// <summary>
    /// Run the full re-encode pipeline over <paramref name="input"/>. On success carries a rewindable
    /// canonical-JPEG <see cref="Jpeg"/> stream (caller disposes) plus the output dimensions; on failure
    /// carries the client-facing <see cref="Error"/> (<c>415</c> non-image / wrong format, <c>413</c>
    /// pixel bomb) and no stream.
    /// </summary>
    public sealed record Result
    {
        public MemoryStream? Jpeg { get; private init; }
        public int Width { get; private init; }
        public int Height { get; private init; }
        public IResult? Error { get; private init; }
        public bool Ok => Error is null;

        public static Result Success(MemoryStream jpeg, int width, int height) =>
            new() { Jpeg = jpeg, Width = width, Height = height };

        public static Result Fail(IResult error) => new() { Error = error };
    }

    /// <summary>
    /// Decode → strip → resize → re-encode <paramref name="bytes"/> to a canonical 512×512 JPEG. The
    /// input is treated as fully untrusted: format is sniffed, the header canvas is bounded before the
    /// decode buffer is allocated, and any decode failure maps to <c>415</c> rather than a 500.
    /// </summary>
    public static async Task<Result> ProcessAsync(byte[] bytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
            return Result.Fail(AvatarProblems.UnsupportedMediaType("Empty upload."));

        // (1) Sniff the format from the magic bytes — the client's Content-Type is never trusted. Only
        //     the two formats the §1.9 decoder handles natively are accepted (no HEIC: converted client-side).
        using var probe = new MemoryStream(bytes, writable: false);
        IImageFormat format;
        try
        {
            format = await Image.DetectFormatAsync(probe, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Fail(AvatarProblems.UnsupportedMediaType("Not a recognized image."));
        }

        if (format is not (JpegFormat or PngFormat))
            return Result.Fail(AvatarProblems.UnsupportedMediaType("Only image/jpeg and image/png are accepted."));

        // (2) Decompression-bomb guard: read ONLY the header dimensions and reject on the pixel/edge cap
        //     before allocating a full-resolution decode buffer.
        probe.Position = 0;
        ImageInfo info;
        try
        {
            info = await Image.IdentifyAsync(probe, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Fail(AvatarProblems.UnsupportedMediaType("Not a recognized image."));
        }

        if (info.Width <= 0 || info.Height <= 0
            || info.Width > MaxInputDimension || info.Height > MaxInputDimension
            || (long)info.Width * info.Height > MaxInputPixels)
        {
            return Result.Fail(AvatarProblems.BlobTooLarge("Image dimensions exceed the allowed pixel budget."));
        }

        // (3) Decode with metadata skipped (EXIF never even materializes), then re-encode from scratch.
        var decoderOptions = new DecoderOptions { SkipMetadata = true };
        using var source = new MemoryStream(bytes, writable: false);
        Image image;
        try
        {
            image = await Image.LoadAsync(decoderOptions, source, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Fail(AvatarProblems.UnsupportedMediaType("The image could not be decoded."));
        }

        try
        {
            // Belt-and-suspenders even with SkipMetadata: guarantee no PII segment survives the re-encode.
            image.Metadata.ExifProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.XmpProfile = null;
            image.Metadata.IccProfile = null;

            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(TargetSize, TargetSize),
                Mode = ResizeMode.Crop,              // cover-crop to an exact square
                Position = AnchorPositionMode.Center,
            }));

            var output = new MemoryStream();
            await image.SaveAsJpegAsync(output, new JpegEncoder { Quality = JpegQuality }, ct);
            output.Position = 0;
            return Result.Success(output, image.Width, image.Height);
        }
        finally
        {
            image.Dispose();
        }
    }
}

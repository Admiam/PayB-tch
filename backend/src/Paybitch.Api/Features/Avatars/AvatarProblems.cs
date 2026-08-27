using Microsoft.AspNetCore.Http;
using Paybitch.Api.Common.Errors;

namespace Paybitch.Api.Features.Avatars;

/// <summary>
/// E7 problem shapes not yet in the shared <see cref="ProblemCodes"/> catalog (§1.8 new codes). The
/// strings mirror the Appendix B extensions catalog; they are referenced here as literals until the
/// catalog gains them (see the convergence note). <see cref="Problems.Create"/> resolves a title from
/// the catalog and falls back to the code itself, so an unregistered code still renders a valid body.
/// </summary>
public static class AvatarProblems
{
    /// <summary><c>mime</c> not on the JPEG/PNG allowlist, or the bytes do not decode as a real image.</summary>
    public const string UnsupportedMediaTypeCode = "unsupported_media_type";

    /// <summary>Upload over the byte cap, or a declared canvas over the decompression-bomb pixel ceiling.</summary>
    public const string BlobTooLargeCode = "blob_too_large";

    /// <summary>415 — the sniffed format is not JPEG/PNG, or the payload is not a decodable image.</summary>
    public static IResult UnsupportedMediaType(string? detail = null) =>
        Problems.Create(StatusCodes.Status415UnsupportedMediaType, UnsupportedMediaTypeCode, detail: detail);

    /// <summary>413 — the upload exceeds the byte cap or the decode-time pixel ceiling.</summary>
    public static IResult BlobTooLarge(string? detail = null) =>
        Problems.Create(StatusCodes.Status413PayloadTooLarge, BlobTooLargeCode, detail: detail);
}

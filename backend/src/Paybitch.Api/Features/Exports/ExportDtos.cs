namespace Paybitch.Api.Features.Exports;

/// <summary>
/// <c>POST /groups/{g}/exports</c> body (EXT-D4a/d). Explicit DTO — never bound to an entity (§4.7
/// over-post). <see cref="Kind"/> is an allowlist (<c>csv</c>/<c>pdf</c>); <see cref="From"/>/<see cref="To"/>
/// are optional ISO dates; <see cref="Locale"/> selects the CSV dialect for a <c>csv</c> build.
/// </summary>
public sealed record CreateExportRequest(
    string? Kind,
    string? From,
    string? To,
    string? Locale);

/// <summary>The <c>202 Accepted</c> body for an enqueued async export (group or GDPR).</summary>
public sealed record EnqueuedExportResponse(string Id, string Kind, string Status);

/// <summary>
/// <c>GET /exports/{id}</c> poll projection (requester-scoped). The artifact metadata
/// (<see cref="ContentType"/>/<see cref="ByteSize"/>/<see cref="Download"/>) is present only once
/// <see cref="Status"/> is <c>ready</c> and the artifact has not expired.
/// </summary>
public sealed record ExportStatusResponse(
    string Id,
    string Kind,
    string Status,
    string? ContentType,
    long? ByteSize,
    DateTimeOffset ExpiresAt,
    string? Download);

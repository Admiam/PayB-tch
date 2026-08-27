namespace Paybitch.Api.Features.Comments;

/// <summary>
/// POST body (§7.4). Binds ONLY <c>{clientId, body}</c> — <c>authorUser</c>, <c>version</c>, timestamps
/// and <c>deletedAt</c> are server-owned; an <c>author</c> field does not exist on the wire, so a body
/// claiming a different author is impossible (over-posting guard, EXT-DC7). <c>clientId</c> drives the
/// (groupId, clientId) create idempotency (D9/X1).
/// </summary>
public sealed record CreateCommentRequest
{
    public string? ClientId { get; init; }
    public string? Body { get; init; }
}

/// <summary>PATCH body (§7.4) — an author-only edit binds ONLY <c>{body}</c> (EXT-DC7).</summary>
public sealed record PatchCommentRequest
{
    public string? Body { get; init; }
}

/// <summary>
/// The comment author, hydrated at read time via the author's <c>group_members</c> row in that group
/// (EXT-DC1). <c>displayName</c> resolves to the localized "former member" placeholder once the author is
/// GDPR-anonymized — the comment still renders (EXT-DC2 scrub-in-place, not tombstone).
/// </summary>
public sealed record CommentAuthor(
    string UserId,
    string? MemberId,
    string? DisplayName);

/// <summary>
/// The canonical comment representation returned by every comment route and embedded in <c>/sync</c> as
/// the <c>comment</c> entity (§7.4, §7.5). Money-inert by construction — there is no <c>amount</c>/
/// <c>currency</c> anywhere (§7.1). <c>edited</c> is <c>version &gt; 1</c> (equivalently
/// <c>updatedAt &gt; createdAt</c>, EXT-DC5).
/// </summary>
public sealed record CommentResponse(
    string Id,
    string? ClientId,
    string GroupId,
    string ExpenseId,
    CommentAuthor Author,
    string Body,
    string CreatedAt,
    string UpdatedAt,
    bool Edited,
    int Version,
    bool Deleted);

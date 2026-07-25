namespace Paybitch.Api.Features.Avatars;

/// <summary>
/// <c>POST /me/avatar</c> success body. The user avatar is a <c>/me</c>-only singleton (EXT-D1l) with no
/// version column, so the client re-fetches the image from <see cref="Href"/> (cache-busted by the ETag
/// on the GET, which tracks the server-minted key).
/// </summary>
public sealed record AvatarSetResponse(string Href);

/// <summary>
/// <c>POST</c> success body for the group-scoped images (member image, group icon). <see cref="Version"/>
/// is the bumped aggregate version that also rides <c>/sync</c> on the existing <c>member</c>/<c>group</c>
/// entity (EXT-D1l — E7 adds no new sync entity); <see cref="Href"/> is where to GET the bytes. The
/// storage key itself is never returned (EXT-D1m).
/// </summary>
public sealed record ImageSetResponse(string Href, int Version);

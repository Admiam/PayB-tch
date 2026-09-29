using FluentValidation;
using Paybitch.Api.Features.Groups.Support;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Groups.Members;

// --- Requests ---

/// <summary><c>POST /groups/{g}/members</c> — add a ghost participant (§3.8).</summary>
public sealed record AddMemberRequest(string DisplayName, string? IconSymbol);

/// <summary><c>PATCH /groups/{g}/members/{m}</c> — self rename / icon. Image URLs are not writable (§3.1).</summary>
public sealed record PatchMemberRequest(string? DisplayName, string? IconSymbol);

/// <summary><c>PUT /groups/{g}/members/{m}/role</c> body (§3.8.2).</summary>
public sealed record ChangeRoleRequest(string Role);

// --- Response ---

/// <summary>
/// A participant row. <c>isGhost</c> = unlinked slot (<c>user_id NULL</c>); <c>deleted</c> = soft-removed;
/// <c>isMe</c> = this row belongs to the calling account; <c>linkKey</c> = an opaque per-caller handle
/// for the account behind the row, so the same human can be recognised across groups.
/// </summary>
/// <remarks>
/// <c>isMe</c> exists because a client otherwise cannot tell which participant it is. The row's
/// <c>user_id</c> is deliberately never published — it would leak account identity across a group — so
/// before this flag a client had to guess by matching display names, which is ambiguous the moment two
/// real users share a name and wrong whenever someone renames their row. The server already knows the
/// answer for free; publishing just the boolean settles it without exposing the id.
///
/// <c>linkKey</c> generalises that to everyone else in the room, for the same reason and under the same
/// constraint: see <see cref="Support.MemberLinkKeys"/> for why it is an HMAC rather than the id. Null
/// for a ghost, which has no account to name — and those are precisely the rows a human has to pair by
/// hand.
/// </remarks>
public sealed record MemberResponse(
    Guid Id,
    Guid GroupId,
    string DisplayName,
    string Role,
    string? IconSymbol,
    bool IsGhost,
    int Version,
    bool Deleted,
    bool IsMe,
    string? LinkKey)
{
    /// <param name="callerUserId">
    /// The authenticated account, used to compute <see cref="IsMe"/>. Null in contexts with no
    /// caller, where every row reports <c>isMe: false</c>.
    /// </param>
    /// <param name="links">
    /// Issuer for <see cref="LinkKey"/>. Omitted where cross-group identity is not in play — a single
    /// mutated row tells a client nothing it can pair against — and the key then reads as null rather
    /// than as "this member has no account".
    /// </param>
    public static MemberResponse From(
        GroupMember m, Guid? callerUserId = null, MemberLinkKeys? links = null) => new(
        m.Id, m.GroupId, m.DisplayName, m.Role, m.IconSymbol,
        m.UserId is null, m.Version, m.DeletedAt is not null,
        m.UserId is not null && m.UserId == callerUserId,
        links?.For(callerUserId, m.UserId));
}

// --- Validators ---

public sealed class AddMemberRequestValidator : AbstractValidator<AddMemberRequest>
{
    public AddMemberRequestValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.IconSymbol).MaximumLength(100).When(x => x.IconSymbol is not null);
    }
}

public sealed class PatchMemberRequestValidator : AbstractValidator<PatchMemberRequest>
{
    public PatchMemberRequestValidator()
    {
        When(x => x.DisplayName is not null, () =>
            RuleFor(x => x.DisplayName!).NotEmpty().MaximumLength(100));
        RuleFor(x => x.IconSymbol).MaximumLength(100).When(x => x.IconSymbol is not null);
    }
}

public sealed class ChangeRoleRequestValidator : AbstractValidator<ChangeRoleRequest>
{
    public ChangeRoleRequestValidator()
    {
        RuleFor(x => x.Role)
            .Must(MembershipOps.IsValidRole)
            .WithMessage("role must be one of 'owner', 'admin', 'member'.");
    }
}

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

/// <summary>A participant row. <c>isGhost</c> = unlinked slot (<c>user_id NULL</c>); <c>deleted</c> = soft-removed.</summary>
public sealed record MemberResponse(
    Guid Id,
    Guid GroupId,
    string DisplayName,
    string Role,
    string? IconSymbol,
    bool IsGhost,
    int Version,
    bool Deleted)
{
    public static MemberResponse From(GroupMember m) => new(
        m.Id, m.GroupId, m.DisplayName, m.Role, m.IconSymbol,
        m.UserId is null, m.Version, m.DeletedAt is not null);
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

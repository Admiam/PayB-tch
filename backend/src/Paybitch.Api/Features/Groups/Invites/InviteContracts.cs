using FluentValidation;
using Paybitch.Api.Features.Groups.Groups;

namespace Paybitch.Api.Features.Groups.Invites;

// --- Requests ---

/// <summary>
/// <c>POST /groups/{g}/invites</c> body (all optional). <c>memberId</c> set ⇒ a ghost-claim invite
/// (§3.8.1); absent ⇒ a generic join invite. <c>email</c> is informational only in link-only v1 (§3.12).
/// </summary>
public sealed record CreateInviteRequest(Guid? MemberId, string? Email);

/// <summary><c>POST /invites/{token}/accept</c> body — both fields required for a ghost claim (§3.8.1).</summary>
public sealed record AcceptInviteRequest(Guid? ClaimMemberId, bool? AcceptInheritedLedger);

// --- Responses ---

/// <summary>Invite create result — the raw <c>token</c> and share <c>link</c> are shown exactly once.</summary>
public sealed record CreateInviteResponse(Guid Id, string Token, string Link, Guid? MemberId, DateTimeOffset ExpiresAt);

/// <summary>Invite list row — never the token or its hash.</summary>
public sealed record InviteListItemResponse(
    Guid Id, Guid? MemberId, string? Email, Guid? InvitedBy,
    DateTimeOffset ExpiresAt, DateTimeOffset? AcceptedAt, DateTimeOffset CreatedAt);

/// <summary>Unauthenticated preview — D6 no-leak: strangers see only what informed consent needs (§3.8.1).</summary>
public sealed record InvitePreviewResponse(GroupPreview Group, string? InvitedBy, DateTimeOffset ExpiresAt, ClaimPreview? Claim);

public sealed record GroupPreview(string Name, int MemberCount, string DefaultCurrency);

/// <summary>Ghost-claim preview — the ghost's own name + its OWN per-currency nets, nothing wider.</summary>
public sealed record ClaimPreview(Guid MemberId, string DisplayName, IReadOnlyList<CurrencyNetResponse> NetByCurrency);

/// <summary>Accept result — the caller's now-active membership.</summary>
public sealed record AcceptInviteResponse(Guid GroupId, Guid MemberId, string Role);

// --- Validators ---

public sealed class CreateInviteRequestValidator : AbstractValidator<CreateInviteRequest>
{
    public CreateInviteRequestValidator()
    {
        When(x => !string.IsNullOrEmpty(x.Email), () =>
            RuleFor(x => x.Email!).EmailAddress().MaximumLength(320));
    }
}

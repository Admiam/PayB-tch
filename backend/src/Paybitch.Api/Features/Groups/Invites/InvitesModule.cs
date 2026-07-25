using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Common.Options;
using Paybitch.Api.Common.RateLimiting;
using Paybitch.Api.Common.Validation;
using Paybitch.Api.Features.Groups.Groups;
using Paybitch.Api.Features.Groups.Support;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Groups.Invites;

/// <summary>
/// Invites (§3.8.1, §3.12 link-only). Only a SHA-256 hash of the single-use token is stored. Preview is
/// unauthenticated + IP rate-limited (D6 no-leak). Accept is token-authorized (no D6 filter) — it checks
/// archived status inside the handler and runs everything in one transaction, consuming the invite first.
/// Invites are deliberately NOT synced (§3.5.1), so no <c>change_log</c> row on create/delete — only the
/// resulting <c>member</c> + <c>access</c> rows on accept.
/// </summary>
public sealed class InvitesModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapPost("/groups/{groupId}/invites", CreateInvite)
            .RequireGroupMembership()
            .RequireGroupNotArchived()
            .RequireRateLimiting(RateLimitPolicies.InviteCreate)
            .WithValidation()
            .WithName("CreateInvite")
            .WithSummary("Create a link invite (generic or ghost-claim).")
            .WithTags("Invites");

        app.MapGet("/groups/{groupId}/invites", ListInvites)
            .RequireGroupMembership()
            .WithName("ListInvites")
            .WithSummary("List a group's invites.")
            .WithTags("Invites");

        app.MapDelete("/groups/{groupId}/invites/{inviteId}", DeleteInvite)
            .RequireGroupMembership()
            .WithName("DeleteInvite")
            .WithSummary("Revoke (hard-delete) an unaccepted invite.")
            .WithTags("Invites");

        // Token-authorized routes — top level, no group membership filter (§3.8.1).
        app.MapGet("/invites/{token}", PreviewInvite)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.InvitePreview)
            .WithName("PreviewInvite")
            .WithSummary("Preview an invite (unauthenticated, IP rate-limited).")
            .WithTags("Invites");

        app.MapPost("/invites/{token}/accept", AcceptInvite)
            .WithName("AcceptInvite")
            .WithSummary("Join a group or claim a ghost via an invite token.")
            .WithTags("Invites");
    }

    // --- POST /groups/{g}/invites ---
    private static async Task<IResult> CreateInvite(
        CreateInviteRequest? req, HttpContext http, AppDbContext db,
        IClock clock, IOptions<OperationalConstants> opsOpt, CancellationToken ct)
    {
        var membership = http.GetMembership();
        var userId = http.GetUserId();
        var ops = opsOpt.Value;
        var memberId = req?.MemberId;

        if (memberId is Guid ghostId)
        {
            // Ghost-claim invite: target must be an active, unclaimed ghost of THIS group (§3.8.1).
            var ghost = await db.GroupMembers
                .FirstOrDefaultAsync(m => m.Id == ghostId && m.GroupId == membership.GroupId && m.DeletedAt == null, ct);
            if (ghost is null)
                return Problems.NotFound();
            if (ghost.UserId is not null)
                return Problems.Validation(ProblemCodes.NotAGhost, "memberId", "Target member is not an unclaimed ghost.");
        }

        var token = InviteTokens.NewToken();
        var email = string.IsNullOrWhiteSpace(req?.Email) ? null : req!.Email!.Trim();
        var expiresAt = clock.UtcNow + ops.InviteTtl;

        var invite = new Invite
        {
            GroupId = membership.GroupId,
            TokenHash = InviteTokens.Hash(token),
            Email = email,
            MemberId = memberId,
            InvitedBy = userId,
            ExpiresAt = expiresAt,
            CreatedAt = clock.UtcNow,
        };
        db.Invites.Add(invite);
        await db.SaveChangesAsync(ct); // invites are not synced — no change_log row (§3.5.1)

        var link = $"{http.Request.Scheme}://{http.Request.Host}/v1/invites/{token}";
        return Results.Created(
            $"/v1/groups/{membership.GroupId}/invites/{invite.Id}",
            new CreateInviteResponse(invite.Id, token, link, memberId, expiresAt));
    }

    // --- GET /groups/{g}/invites ---
    private static async Task<IResult> ListInvites(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var membership = http.GetMembership();
        var invites = await db.Invites.AsNoTracking()
            .Where(i => i.GroupId == membership.GroupId)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new InviteListItemResponse(
                i.Id, i.MemberId, i.Email, i.InvitedBy, i.ExpiresAt, i.AcceptedAt, i.CreatedAt))
            .ToListAsync(ct);
        return Results.Ok(invites);
    }

    // --- DELETE /groups/{g}/invites/{inviteId} ---
    private static async Task<IResult> DeleteInvite(
        Guid inviteId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var membership = http.GetMembership();
        var invite = await db.Invites
            .FirstOrDefaultAsync(i => i.Id == inviteId && i.GroupId == membership.GroupId, ct);
        if (invite is null)
            return Problems.NotFound();
        if (invite.AcceptedAt is not null)
            return Problems.Conflict(ProblemCodes.InviteAlreadyAccepted);

        db.Invites.Remove(invite); // hard delete (§3.12)
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // --- GET /invites/{token} (preview) ---
    private static async Task<IResult> PreviewInvite(
        string token, AppDbContext db, IClock clock, CancellationToken ct)
    {
        var hash = InviteTokens.Hash(token);
        var invite = await db.Invites.AsNoTracking().FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invite is null)
            return Problems.NotFound(); // unknown or revoked — no probe oracle (§3.8.1)
        if (invite.ExpiresAt <= clock.UtcNow)
            return Problems.Gone(ProblemCodes.InviteExpired);

        var group = await db.Groups.AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == invite.GroupId && g.DeletedAt == null, ct);
        if (group is null)
            return Problems.NotFound();

        var memberCount = await MembershipOps.CountActiveMembersAsync(db, group.Id, ct);
        var invitedBy = invite.InvitedBy is Guid inviter
            ? await db.Users.AsNoTracking().Where(u => u.Id == inviter).Select(u => u.DisplayName).FirstOrDefaultAsync(ct)
            : null;

        ClaimPreview? claim = null;
        if (invite.MemberId is Guid ghostId)
        {
            var ghost = await db.GroupMembers.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == ghostId && m.GroupId == group.Id && m.DeletedAt == null, ct);
            if (ghost is not null)
            {
                var nets = await LedgerBalance.ComputeMemberNetAsync(db, ghost.Id, ct);
                claim = new ClaimPreview(ghost.Id, ghost.DisplayName, ToCurrencyNets(nets));
            }
        }

        return Results.Ok(new InvitePreviewResponse(
            new GroupPreview(group.Name, memberCount, group.DefaultCurrency), invitedBy, invite.ExpiresAt, claim));
    }

    // --- POST /invites/{token}/accept ---
    private static async Task<IResult> AcceptInvite(
        string token, AcceptInviteRequest? req, HttpContext http, AppDbContext db,
        IChangeLogWriter changeLog, IClock clock, IOptions<OperationalConstants> opsOpt, CancellationToken ct)
    {
        var userId = http.GetUserId();
        var ops = opsOpt.Value;
        var hash = InviteTokens.Hash(token);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var invite = await db.Invites.FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invite is null)
            return Problems.NotFound();
        if (invite.ExpiresAt <= clock.UtcNow)
            return Problems.Gone(ProblemCodes.InviteExpired);

        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == invite.GroupId && g.DeletedAt == null, ct);
        if (group is null)
            return Problems.NotFound();
        // Token-authorized route: archived check lives IN the handler (§3.7).
        if (group.ArchivedAt is not null)
            return GroupErrors.GroupArchived();

        var isGhost = invite.MemberId is not null;

        // Body preconditions for a ghost claim (§3.8.1).
        if (isGhost)
        {
            if (req?.ClaimMemberId != invite.MemberId)
                return Problems.Validation(ProblemCodes.ClaimMemberMismatch, "claimMemberId", "claimMemberId does not match the invite.");
            if (req?.AcceptInheritedLedger != true)
                return Problems.Validation(ProblemCodes.LedgerAcceptanceRequired, "acceptInheritedLedger", "Inherited ledger must be explicitly accepted.");
        }

        var existing = await db.GroupMembers
            .FirstOrDefaultAsync(m => m.GroupId == group.Id && m.UserId == userId, ct);

        // Already-consumed → idempotent same-user replay, else the token is spent (§3.8.1).
        if (invite.AcceptedAt is not null)
        {
            if (isGhost)
            {
                var claimed = await db.GroupMembers.AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Id == invite.MemberId, ct);
                if (claimed is not null && claimed.UserId == userId)
                    return Results.Ok(new AcceptInviteResponse(group.Id, claimed.Id, claimed.Role));
                return Problems.Gone(ProblemCodes.InviteConsumed);
            }

            if (existing is not null && existing.DeletedAt is null)
                return Results.Ok(new AcceptInviteResponse(group.Id, existing.Id, existing.Role));
            return Problems.Gone(ProblemCodes.InviteConsumed);
        }

        // already_member — one human = one member row per group (§3.8.1).
        if (isGhost)
        {
            if (existing is not null)
                return Problems.Conflict(ProblemCodes.AlreadyMember);
        }
        else if (existing is not null && existing.DeletedAt is null)
        {
            return Problems.Conflict(ProblemCodes.AlreadyMember);
        }

        // Per-user group cap (§Appendix A) — the accept always yields a new active membership.
        var activeMemberships = await MembershipOps.CountActiveMembershipsAsync(db, userId, ct);
        if (activeMemberships >= ops.GroupsPerUser)
            return Problems.Conflict(ProblemCodes.LimitExceeded);

        // Consume the invite first — 0 rows ⇒ lost the race (§3.8.1).
        var consumed = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE invites SET accepted_at = {clock.UtcNow} WHERE id = {invite.Id} AND accepted_at IS NULL", ct);
        if (consumed == 0)
            return Problems.Gone(ProblemCodes.InviteConsumed);

        Guid resultMemberId;
        string resultRole;

        if (isGhost)
        {
            var ghostMemberId = invite.MemberId!.Value;

            // Link the ghost; 0 rows ⇒ another invite claimed it (our token stays valid on rollback).
            var linked = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE group_members
                 SET user_id = {userId}, role = 'member', former_user_id = NULL,
                     version = version + 1, updated_at = {clock.UtcNow}
                 WHERE id = {ghostMemberId} AND user_id IS NULL AND deleted_at IS NULL
                 """, ct);
            if (linked == 0)
                return Problems.Conflict(ProblemCodes.GhostAlreadyClaimed);

            resultMemberId = ghostMemberId;
            resultRole = MembershipOps.RoleMember;
        }
        else if (existing is not null)
        {
            // Generic; the caller's soft-deleted row is reactivated (respects UNIQUE(group_id,user_id)).
            existing.DeletedAt = null;
            existing.Version += 1;
            resultMemberId = existing.Id;
            resultRole = existing.Role;
        }
        else
        {
            var displayName = await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
            var member = new GroupMember
            {
                GroupId = group.Id,
                UserId = userId,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Me" : displayName!,
                Role = MembershipOps.RoleMember,
                CreatedAt = clock.UtcNow,
                Version = 1,
            };
            db.GroupMembers.Add(member);
            resultMemberId = member.Id;
            resultRole = member.Role;
        }

        changeLog.Append(group.Id, ChangeLogEntityTypes.Member, resultMemberId, isDelete: false);
        changeLog.AppendAccess(group.Id, userId, isRevoke: false);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent insert won the UNIQUE(group_id,user_id) race (§3.8.1 backstop).
            return Problems.Conflict(ProblemCodes.AlreadyMember);
        }

        await tx.CommitAsync(ct);
        return Results.Ok(new AcceptInviteResponse(group.Id, resultMemberId, resultRole));
    }

    private static IReadOnlyList<CurrencyNetResponse> ToCurrencyNets(IReadOnlyDictionary<string, long> nets)
        => nets
            .Where(kv => kv.Value != 0)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new CurrencyNetResponse(kv.Key, kv.Value.ToString(CultureInfo.InvariantCulture)))
            .ToList();
}

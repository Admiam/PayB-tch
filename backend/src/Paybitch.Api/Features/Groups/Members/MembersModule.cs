using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Common.Options;
using Paybitch.Api.Common.Validation;
using Paybitch.Api.Features.Groups.Support;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Groups.Members;

/// <summary>
/// Participant lifecycle (§3.8). Add is admin-only (ghost); self-edit is member-only; role changes and
/// removal enforce the rank + ≥1-owner invariants (§3.8.2/§3.8.3). Leave is a state transition (unlink →
/// ghost), never a delete; remove is a soft-delete gated on a zero balance under a <c>FOR UPDATE</c> lock.
/// </summary>
public sealed class MembersModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        const string Base = "/groups/{groupId}/members";

        app.MapGet(Base, ListMembers)
            .RequireGroupMembership()
            .WithName("ListMembers")
            .WithSummary("List participants (soft-deleted omitted unless ?includeDeleted=true).")
            .WithTags("Members");

        app.MapPost(Base, AddMember)
            .RequireGroupAdmin()
            .RequireGroupNotArchived()
            .WithValidation()
            .WithName("AddMember")
            .WithSummary("Add a ghost participant (admin).")
            .WithTags("Members");

        // Static "me/leave" is declared before "{memberId}" so it wins the more-specific match.
        app.MapPost($"{Base}/me/leave", LeaveGroup)
            .RequireGroupMembership()
            .RequireGroupNotArchived()
            .WithName("LeaveGroup")
            .WithSummary("Leave the group; your row reverts to a ghost, ledger preserved.")
            .WithTags("Members");

        app.MapPatch($"{Base}/{{memberId}}", PatchMember)
            .RequireGroupMembership()
            .RequireGroupNotArchived()
            .WithValidation()
            .WithName("PatchMember")
            .WithSummary("Rename / re-icon your own participant row.")
            .WithTags("Members");

        app.MapPut($"{Base}/{{memberId}}/role", ChangeRole)
            .RequireGroupAdmin()
            .RequireGroupNotArchived()
            .WithValidation()
            .WithName("ChangeMemberRole")
            .WithSummary("Change a participant's role (admin; owner grants owner/admin).")
            .WithTags("Members");

        app.MapDelete($"{Base}/{{memberId}}", RemoveMember)
            .RequireGroupAdmin()
            .RequireGroupNotArchived()
            .WithName("RemoveMember")
            .WithSummary("Remove a participant (admin; all balances must be zero).")
            .WithTags("Members");
    }

    // --- GET /members ---
    private static async Task<IResult> ListMembers(
        HttpContext http, AppDbContext db, MemberLinkKeys links, CancellationToken ct)
    {
        var membership = http.GetMembership();
        var includeDeleted = string.Equals(http.Request.Query["includeDeleted"], "true", StringComparison.OrdinalIgnoreCase);

        var query = db.GroupMembers.AsNoTracking().Where(m => m.GroupId == membership.GroupId);
        if (!includeDeleted)
            query = query.Where(m => m.DeletedAt == null);

        var members = await query
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .ToListAsync(ct);

        // The roster is the one read where cross-group identity matters: it is what a client pairs
        // against, so it is the only place that spends the HMAC.
        var callerUserId = http.GetUserId();
        return Results.Ok(members.Select(m => MemberResponse.From(m, callerUserId, links)).ToList());
    }

    // --- POST /members (add ghost) ---
    private static async Task<IResult> AddMember(
        AddMemberRequest req, HttpContext http, AppDbContext db,
        IChangeLogWriter changeLog, IClock clock, IOptions<OperationalConstants> opsOpt, CancellationToken ct)
    {
        var membership = http.GetMembership();
        var ops = opsOpt.Value;

        var active = await MembershipOps.CountActiveMembersAsync(db, membership.GroupId, ct);
        if (active >= ops.MembersPerGroup)
            return Problems.Conflict(ProblemCodes.LimitExceeded);

        var member = new GroupMember
        {
            GroupId = membership.GroupId,
            UserId = null, // ghost
            DisplayName = req.DisplayName.Trim(),
            Role = MembershipOps.RoleMember,
            IconSymbol = string.IsNullOrEmpty(req.IconSymbol) ? null : req.IconSymbol,
            CreatedAt = clock.UtcNow,
            Version = 1,
        };
        db.GroupMembers.Add(member);
        changeLog.Append(membership.GroupId, ChangeLogEntityTypes.Member, member.Id, isDelete: false);
        await db.SaveChangesAsync(ct);

        ConcurrencyHeaders.SetETag(http.Response, member.Version);
        return Results.Created($"/v1/groups/{membership.GroupId}/members/{member.Id}", MemberResponse.From(member, http.GetUserId()));
    }

    // --- PATCH /members/{memberId} (self) ---
    private static async Task<IResult> PatchMember(
        Guid memberId, PatchMemberRequest req, HttpContext http, AppDbContext db,
        IChangeLogWriter changeLog, CancellationToken ct)
    {
        var membership = http.GetMembership();

        // Self-service only; role changes go through …/role (§3.1).
        if (memberId != membership.MemberId)
            return Problems.Forbidden(ProblemCodes.InsufficientRole);

        var member = await db.GroupMembers
            .FirstOrDefaultAsync(m => m.Id == memberId && m.GroupId == membership.GroupId && m.DeletedAt == null, ct);
        if (member is null)
            return Problems.NotFound();

        var changed = false;
        if (req.DisplayName is not null)
        {
            var trimmed = req.DisplayName.Trim();
            if (trimmed != member.DisplayName) { member.DisplayName = trimmed; changed = true; }
        }
        if (req.IconSymbol is not null)
        {
            var icon = string.IsNullOrEmpty(req.IconSymbol) ? null : req.IconSymbol;
            if (icon != member.IconSymbol) { member.IconSymbol = icon; changed = true; }
        }

        if (changed)
        {
            member.Version += 1;
            changeLog.Append(membership.GroupId, ChangeLogEntityTypes.Member, member.Id, isDelete: false);
            await db.SaveChangesAsync(ct);
        }

        ConcurrencyHeaders.SetETag(http.Response, member.Version);
        return Results.Ok(MemberResponse.From(member, http.GetUserId()));
    }

    // --- PUT /members/{memberId}/role ---
    private static async Task<IResult> ChangeRole(
        Guid memberId, ChangeRoleRequest req, HttpContext http, AppDbContext db,
        IChangeLogWriter changeLog, CancellationToken ct)
    {
        var membership = http.GetMembership();

        if (!ConcurrencyHeaders.TryReadIfMatch(http.Request, out var ifMatch))
            return Problems.PreconditionRequired();

        var member = await db.GroupMembers
            .FirstOrDefaultAsync(m => m.Id == memberId && m.GroupId == membership.GroupId && m.DeletedAt == null, ct);
        if (member is null)
            return Problems.NotFound();

        if (member.Version != ifMatch)
            return Problems.VersionConflict(MemberResponse.From(member, http.GetUserId()));

        var newRole = req.Role;

        // A ghost can never hold a privileged role (§3.8.2 — CHECK-backed).
        if (member.UserId is null && newRole != MembershipOps.RoleMember)
            return Problems.Validation(ProblemCodes.GhostCannotHoldRole, "role", "A ghost member cannot hold a role.");

        // Same role — idempotent no-op.
        if (member.Role == newRole)
        {
            ConcurrencyHeaders.SetETag(http.Response, member.Version);
            return Results.Ok(MemberResponse.From(member, http.GetUserId()));
        }

        // Granting or revoking owner/admin requires an owner caller (§3.8.2).
        var touchesPrivilege = MembershipOps.IsPrivileged(newRole) || MembershipOps.IsPrivileged(member.Role);
        if (touchesPrivilege && membership.Role != MembershipOps.RoleOwner)
            return Problems.Forbidden(ProblemCodes.InsufficientRole);

        // Demoting the last owner is refused (§1.5 backstop).
        if (member.Role == MembershipOps.RoleOwner && newRole != MembershipOps.RoleOwner)
        {
            var remainingOwners = await MembershipOps.CountActiveOwnersAsync(db, membership.GroupId, member.Id, ct);
            if (remainingOwners == 0)
                return Problems.Conflict(ProblemCodes.LastOwner);
        }

        member.Role = newRole;
        member.Version += 1;
        changeLog.Append(membership.GroupId, ChangeLogEntityTypes.Member, member.Id, isDelete: false);
        await db.SaveChangesAsync(ct);

        ConcurrencyHeaders.SetETag(http.Response, member.Version);
        return Results.Ok(MemberResponse.From(member, http.GetUserId()));
    }

    // --- POST /members/me/leave ---
    private static async Task<IResult> LeaveGroup(
        HttpContext http, AppDbContext db, IChangeLogWriter changeLog, CancellationToken ct)
    {
        var membership = http.GetMembership();

        var member = await db.GroupMembers
            .FirstOrDefaultAsync(m => m.Id == membership.MemberId && m.DeletedAt == null, ct);
        if (member is null)
            return Problems.NotFound();

        // Last owner must transfer first (§3.8.2/§3.8.3).
        if (member.Role == MembershipOps.RoleOwner)
        {
            var remainingOwners = await MembershipOps.CountActiveOwnersAsync(db, membership.GroupId, member.Id, ct);
            if (remainingOwners == 0)
                return Problems.Conflict(ProblemCodes.LastOwner);
        }

        var formerUserId = member.UserId;
        member.UserId = null;                       // revert to ghost
        member.FormerUserId = formerUserId;         // D7 erase back-reference (§3.8.3)
        member.Role = MembershipOps.RoleMember;     // CHECK-forced for a ghost
        member.Version += 1;

        // Leave is an UPSERT (row stays live) + an access revoke addressed to the leaver (§3.5.2).
        changeLog.Append(membership.GroupId, ChangeLogEntityTypes.Member, member.Id, isDelete: false);
        if (formerUserId is Guid leaver)
            changeLog.AppendAccess(membership.GroupId, leaver, isRevoke: true);

        await db.SaveChangesAsync(ct);

        ConcurrencyHeaders.SetETag(http.Response, member.Version);
        return Results.Ok(MemberResponse.From(member, http.GetUserId()));
    }

    // --- DELETE /members/{memberId} (remove) ---
    private static async Task<IResult> RemoveMember(
        Guid memberId, HttpContext http, AppDbContext db,
        IChangeLogWriter changeLog, IClock clock, CancellationToken ct)
    {
        var membership = http.GetMembership();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // §3.8.3: lock the target member row FIRST, then compute the buckets (serialized vs ledger writes).
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM group_members WHERE id = {memberId} FOR UPDATE", ct);

        var member = await db.GroupMembers
            .FirstOrDefaultAsync(m => m.Id == memberId && m.GroupId == membership.GroupId, ct);
        if (member is null || member.DeletedAt is not null)
        {
            await tx.RollbackAsync(ct);
            return Problems.NotFound();
        }

        // Remove is a strictly stronger revocation than demote: caller rank ≥ target (§3.8.3).
        if (MembershipOps.IsPrivileged(member.Role) && membership.Role != MembershipOps.RoleOwner)
        {
            await tx.RollbackAsync(ct);
            return Problems.Forbidden(ProblemCodes.InsufficientRole);
        }

        if (member.Role == MembershipOps.RoleOwner)
        {
            var remainingOwners = await MembershipOps.CountActiveOwnersAsync(db, membership.GroupId, member.Id, ct);
            if (remainingOwners == 0)
            {
                await tx.RollbackAsync(ct);
                return Problems.Conflict(ProblemCodes.LastOwner);
            }
        }

        var nets = await LedgerBalance.ComputeMemberNetAsync(db, member.Id, ct);
        if (!LedgerBalance.AllZero(nets))
        {
            await tx.RollbackAsync(ct);
            return Problems.Conflict(ProblemCodes.BalanceNotZero);
        }

        member.DeletedAt = clock.UtcNow; // soft-delete, never a hard DELETE (§3.8.3)
        member.Version += 1;

        changeLog.Append(membership.GroupId, ChangeLogEntityTypes.Member, member.Id, isDelete: true); // tombstone
        if (member.UserId is Guid removedUser)
            changeLog.AppendAccess(membership.GroupId, removedUser, isRevoke: true);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Results.NoContent();
    }
}

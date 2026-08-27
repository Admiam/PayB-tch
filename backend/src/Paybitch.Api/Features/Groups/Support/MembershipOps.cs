using Microsoft.EntityFrameworkCore;
using Paybitch.Infrastructure;

namespace Paybitch.Api.Features.Groups.Support;

/// <summary>Roles + membership invariant reads shared across the members/invites handlers (§3.8).</summary>
public static class MembershipOps
{
    public const string RoleOwner = "owner";
    public const string RoleAdmin = "admin";
    public const string RoleMember = "member";

    private static readonly HashSet<string> Roles = new(StringComparer.Ordinal) { RoleOwner, RoleAdmin, RoleMember };
    private static readonly HashSet<string> PrivilegedRoles = new(StringComparer.Ordinal) { RoleOwner, RoleAdmin };

    public static bool IsValidRole(string role) => Roles.Contains(role);
    public static bool IsPrivileged(string role) => PrivilegedRoles.Contains(role);

    /// <summary>Active (non-deleted) member rows in the group — the §Appendix A per-group cap check.</summary>
    public static Task<int> CountActiveMembersAsync(AppDbContext db, Guid groupId, CancellationToken ct)
        => db.GroupMembers.CountAsync(m => m.GroupId == groupId && m.DeletedAt == null, ct);

    /// <summary>
    /// Active owners in the group, optionally excluding one member row (the one about to lose owner via
    /// demote/leave/remove). Backstops the ≥1-owner trigger (§1.5) at the boundary → <c>409 last_owner</c>.
    /// </summary>
    public static Task<int> CountActiveOwnersAsync(AppDbContext db, Guid groupId, Guid? excludeMemberId, CancellationToken ct)
        => db.GroupMembers.CountAsync(
            m => m.GroupId == groupId
                 && m.DeletedAt == null
                 && m.Role == RoleOwner
                 && (excludeMemberId == null || m.Id != excludeMemberId),
            ct);

    /// <summary>Groups a user actively belongs to — the §Appendix A per-user cap check.</summary>
    public static Task<int> CountActiveMembershipsAsync(AppDbContext db, Guid userId, CancellationToken ct)
        => db.GroupMembers.CountAsync(m => m.UserId == userId && m.DeletedAt == null, ct);
}

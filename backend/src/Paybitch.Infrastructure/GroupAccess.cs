using Microsoft.EntityFrameworkCore;
using Paybitch.Infrastructure.Abstractions;

namespace Paybitch.Infrastructure;

/// <summary>D6 membership check backed by the ix_group_members_user index (active rows only).</summary>
public sealed class GroupAccess(AppDbContext db) : IGroupAccess
{
    public Task<MembershipInfo?> GetMembershipAsync(Guid groupId, Guid userId, CancellationToken ct = default)
        => db.GroupMembers
            .Where(m => m.GroupId == groupId && m.UserId == userId && m.DeletedAt == null)
            .Select(m => new MembershipInfo(m.GroupId, m.Id, userId, m.Role))
            .FirstOrDefaultAsync(ct);
}

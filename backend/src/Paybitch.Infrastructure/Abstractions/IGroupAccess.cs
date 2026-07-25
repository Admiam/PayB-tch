namespace Paybitch.Infrastructure.Abstractions;

/// <summary>Result of the D6 membership check: the caller's active member slot in a group.</summary>
public sealed record MembershipInfo(Guid GroupId, Guid MemberId, Guid UserId, string Role);

/// <summary>
/// The single authorization path (D6): an indexed (group_id, user_id) membership lookup that is the
/// first statement of every group-scoped request. A null result ⇒ the caller is not a member; the
/// handler returns 404 (no existence leak).
/// </summary>
public interface IGroupAccess
{
    Task<MembershipInfo?> GetMembershipAsync(Guid groupId, Guid userId, CancellationToken ct = default);
}

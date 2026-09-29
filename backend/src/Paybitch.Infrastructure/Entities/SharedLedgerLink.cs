namespace Paybitch.Infrastructure.Entities;

/// <summary>
/// shared_ledger_links — "this participant row is that same human", as one user sees it.
/// </summary>
/// <remarks>
/// A person only exists inside a group, so the same human in three groups is three
/// <see cref="GroupMember"/> rows with three ids and nothing tying them together. This table is that
/// tie: members sharing a <see cref="PersonId"/> are one person for the purposes of the owner's
/// combined balance. <see cref="PersonId"/> is a client-minted cluster id with no row of its own —
/// there is nothing to store about a person beyond which members it gathers, and the display name is
/// already on those members.
///
/// Scoped to <see cref="UserId"/>: pairings are a private opinion about who is who, never a claim
/// about identity that another member inherits. Two members of the <em>same</em> group can never share
/// a person — they are distinct participants by construction — which the unique index on
/// (user_id, member_id) plus the write-time group check together enforce.
/// </remarks>
public sealed class SharedLedgerLink
{
    public Guid UserId { get; set; }
    public Guid MemberId { get; set; }
    public Guid PersonId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

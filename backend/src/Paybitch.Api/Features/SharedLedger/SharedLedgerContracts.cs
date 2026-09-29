using System.Security.Cryptography;
using System.Text;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.SharedLedger;

// --- Requests ---

/// <summary>
/// One cluster of participant rows the caller says are the same human. <c>Id</c> is client-minted and
/// only has to be stable and unique within the request — it names the cluster, it is not a row.
/// </summary>
public sealed record SharedPersonRequest(Guid? Id, IReadOnlyList<Guid>? MemberIds);

/// <summary><c>PUT /me/shared-ledger</c> — full replace of the caller's combined-view configuration.</summary>
public sealed record UpdateSharedLedgerRequest(
    IReadOnlyList<Guid>? GroupIds,
    IReadOnlyList<SharedPersonRequest>? People);

// --- Responses ---

/// <summary>A stored person: the cluster id plus the member rows it gathers, ascending.</summary>
public sealed record SharedPersonResponse(Guid Id, IReadOnlyList<Guid> MemberIds);

/// <summary>
/// The caller's shared-ledger configuration: which groups are pooled, and who is who across them.
/// </summary>
/// <remarks>
/// No money here. The combined balance is computed from the per-group balance sheets
/// (<c>GET /groups/{g}/balances</c>) the client already has access to — pooling is a view, and
/// materialising it server-side would be a second source of truth for the same arithmetic (D4).
/// </remarks>
public sealed record SharedLedgerResponse(
    int Version,
    IReadOnlyList<Guid> GroupIds,
    IReadOnlyList<SharedPersonResponse> People);

// --- Problem codes ---

/// <summary>
/// E11 problem <c>code</c>s (additive to Appendix B, same posture as <c>CommentProblemCodes</c>:
/// passed as literals through <c>Problems.Validation</c> until the shared catalog absorbs them).
/// </summary>
public static class SharedLedgerProblemCodes
{
    /// <summary>422 — one person claimed two members of the same group; they are distinct participants by construction.</summary>
    public const string SameGroupMembers = "shared_ledger_same_group";

    /// <summary>422 — a member appeared in more than one person, which has no consistent reading.</summary>
    public const string MemberReused = "shared_ledger_member_reused";

    /// <summary>422 — two people shared a cluster id.</summary>
    public const string DuplicatePerson = "shared_ledger_duplicate_person";
}

// --- Version ---

/// <summary>
/// The configuration's ETag: a content hash, not a counter.
/// </summary>
/// <remarks>
/// There is no aggregate row to bump a version on — the config is a set of rows with a composite key —
/// and a content hash needs no storage while still changing on every real edit. Same device-to-device
/// role as the notification-prefs version (D8-lite): a stale <c>If-Match</c> means someone else's
/// browser has since written, so the caller gets a 412 with the current shape to merge against.
/// </remarks>
public static class SharedLedgerVersion
{
    public static int Compute(
        IReadOnlyList<SharedLedgerGroup> groups,
        IReadOnlyList<SharedLedgerLink> links)
    {
        if (groups.Count == 0 && links.Count == 0)
            return 0;

        var normalized = groups
            .Select(g => $"g|{g.GroupId:D}")
            .Concat(links.Select(l => $"l|{l.PersonId:D}|{l.MemberId:D}"))
            .OrderBy(s => s, StringComparer.Ordinal);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", normalized)));

        // First 4 bytes as an unsigned int, masked into the non-negative Int32 range.
        return (int)(BitConverter.ToUInt32(hash, 0) & 0x7FFFFFFF);
    }
}

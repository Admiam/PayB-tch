using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Common.Options;
using Paybitch.Api.Features.Groups.Support;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.SharedLedger;

/// <summary>
/// The caller's combined-view configuration (E11): which groups are pooled into one "who owes whom",
/// and which participant rows across them are the same human.
/// </summary>
/// <remarks>
/// <para>
/// Caller-scoped account settings, like the notification-prefs matrix: private to one account, no
/// <c>change_log</c> row, nothing another member of the group can observe. Pairing Petr's two rows is
/// an opinion about identity, not a claim anyone else inherits — the alternative, a shared "these
/// members are one person" fact, would let one member rewrite what another sees their ledger to mean.
/// </para>
/// <para>
/// <c>PUT</c> is a full replace under one transaction, with an optional <c>If-Match</c>: the config is a
/// set with a composite key and no aggregate row, so a diff protocol would buy nothing but a chance to
/// half-apply. Every referenced group and member is checked against the caller's own membership first,
/// so the endpoint cannot be used to probe for rows the caller cannot already see (D6 — absent and
/// forbidden are the same 404).
/// </para>
/// <para>
/// Reads filter out anything the caller has since lost access to rather than deleting it: a group you
/// left, or a member who was removed, simply stops appearing. The next write — which the client builds
/// from what it read — retires the rows for good, so the config self-heals without a <c>GET</c> that
/// mutates.
/// </para>
/// </remarks>
public sealed class SharedLedgerModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/me/shared-ledger", GetAsync)
            .WithName("GetSharedLedger")
            .WithSummary("Get the caller's pooled groups and cross-group identity pairings.")
            .WithTags("SharedLedger");

        app.MapPut("/me/shared-ledger", PutAsync)
            .WithName("UpdateSharedLedger")
            .WithSummary("Full-replace the caller's shared-ledger configuration (optional If-Match).")
            .WithTags("SharedLedger");
    }

    // --- GET /me/shared-ledger ---
    private static async Task<IResult> GetAsync(
        HttpContext http, AppDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var stored = await LoadAsync(db, currentUser.UserId, ct);
        var response = await ProjectAsync(db, currentUser.UserId, stored, ct);
        ConcurrencyHeaders.SetETag(http.Response, response.Version);
        return Results.Ok(response);
    }

    // --- PUT /me/shared-ledger ---
    private static async Task<IResult> PutAsync(
        UpdateSharedLedgerRequest? request,
        HttpContext http,
        AppDbContext db,
        ICurrentUser currentUser,
        IClock clock,
        IOptions<OperationalConstants> opsOpt,
        CancellationToken ct)
    {
        var userId = currentUser.UserId;
        var reachable = await ReachableGroupIdsAsync(db, userId, ct);

        // 1. Groups: every one must be the caller's own (404 to a non-member, D6).
        var groupIds = (request?.GroupIds ?? []).Distinct().ToList();
        if (groupIds.Any(id => !reachable.Contains(id)))
            return Problems.NotFound();

        // 2. People: shape first, so a contradictory request is refused before anything is read.
        var people = new List<(Guid PersonId, List<Guid> MemberIds)>();
        var seenPersonIds = new HashSet<Guid>();
        var claimedMembers = new HashSet<Guid>();

        foreach (var person in request?.People ?? [])
        {
            var personId = person.Id ?? Guid.CreateVersion7();
            if (!seenPersonIds.Add(personId))
                return Problems.Validation(
                    SharedLedgerProblemCodes.DuplicatePerson, "people",
                    "Two people shared the same id.");

            var memberIds = (person.MemberIds ?? []).Distinct().ToList();
            foreach (var memberId in memberIds)
            {
                if (!claimedMembers.Add(memberId))
                    return Problems.Validation(
                        SharedLedgerProblemCodes.MemberReused, "people",
                        "A member was claimed by more than one person.");
            }

            // A person with one member is the default reading of every unpaired row, so storing it
            // would only be a row that says nothing. Dropped rather than rejected: a client clearing
            // the last pairing off a person is doing something perfectly sensible.
            if (memberIds.Count >= 2)
                people.Add((personId, memberIds));
        }

        var ops = opsOpt.Value;
        var linkCount = people.Sum(p => p.MemberIds.Count);
        if (linkCount > ops.SharedLedgerLinksPerUser)
            return Problems.Conflict(ProblemCodes.LimitExceeded);

        // 3. Members: live rows in groups the caller belongs to, and — within one person — never two
        // from the same group, which would assert that a group has the same human in it twice.
        var members = await db.GroupMembers.AsNoTracking()
            .Where(m => claimedMembers.Contains(m.Id) && m.DeletedAt == null)
            .Select(m => new { m.Id, m.GroupId })
            .ToListAsync(ct);

        var groupOfMember = members.ToDictionary(m => m.Id, m => m.GroupId);
        if (claimedMembers.Any(id =>
                !groupOfMember.TryGetValue(id, out var groupId) || !reachable.Contains(groupId)))
            return Problems.NotFound();

        foreach (var (_, memberIds) in people)
        {
            if (memberIds.Select(id => groupOfMember[id]).Distinct().Count() != memberIds.Count)
                return Problems.Validation(
                    SharedLedgerProblemCodes.SameGroupMembers, "people",
                    "A person cannot hold two members of the same group.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // 4. Optional precondition. Absent ⇒ last-write-wins, which is the right default for one
        // person's own setting on two of their own devices.
        var current = await LoadAsync(db, userId, ct);
        if (ConcurrencyHeaders.TryReadIfMatch(http.Request, out var ifMatch)
            && ifMatch != SharedLedgerVersion.Compute(current.Groups, current.Links))
        {
            return Problems.VersionConflict(await ProjectAsync(db, userId, current, ct));
        }

        await db.SharedLedgerGroups.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
        await db.SharedLedgerLinks.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);

        var now = clock.UtcNow;
        foreach (var groupId in groupIds)
            db.SharedLedgerGroups.Add(new SharedLedgerGroup { UserId = userId, GroupId = groupId, CreatedAt = now });

        foreach (var (personId, memberIds) in people)
            foreach (var memberId in memberIds)
                db.SharedLedgerLinks.Add(new SharedLedgerLink
                {
                    UserId = userId,
                    MemberId = memberId,
                    PersonId = personId,
                    CreatedAt = now,
                });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var saved = await LoadAsync(db, userId, ct);
        var response = await ProjectAsync(db, userId, saved, ct);
        ConcurrencyHeaders.SetETag(http.Response, response.Version);
        return Results.Ok(response);
    }

    // --- helpers ---

    private sealed record StoredConfig(
        List<SharedLedgerGroup> Groups,
        List<SharedLedgerLink> Links);

    private static async Task<StoredConfig> LoadAsync(AppDbContext db, Guid userId, CancellationToken ct) =>
        new(
            await db.SharedLedgerGroups.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct),
            await db.SharedLedgerLinks.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct));

    /// <summary>Group ids the caller is currently an active member of — the whole authorization surface here.</summary>
    private static async Task<HashSet<Guid>> ReachableGroupIdsAsync(
        AppDbContext db, Guid userId, CancellationToken ct) =>
        (await db.GroupMembers.AsNoTracking()
            .Where(m => m.UserId == userId && m.DeletedAt == null)
            .Select(m => m.GroupId)
            .Distinct()
            .ToListAsync(ct))
        .ToHashSet();

    /// <summary>
    /// Stored rows → the wire shape, dropping whatever the caller can no longer see. The version is
    /// computed over the <em>stored</em> rows, not the filtered projection, so an <c>If-Match</c> round
    /// trip still compares like with like.
    /// </summary>
    private static async Task<SharedLedgerResponse> ProjectAsync(
        AppDbContext db, Guid userId, StoredConfig stored, CancellationToken ct)
    {
        var version = SharedLedgerVersion.Compute(stored.Groups, stored.Links);
        if (stored.Groups.Count == 0 && stored.Links.Count == 0)
            return new SharedLedgerResponse(version, [], []);

        var reachable = await ReachableGroupIdsAsync(db, userId, ct);

        var memberIds = stored.Links.Select(l => l.MemberId).Distinct().ToList();
        var liveMembers = await db.GroupMembers.AsNoTracking()
            .Where(m => memberIds.Contains(m.Id) && m.DeletedAt == null)
            .Select(m => new { m.Id, m.GroupId })
            .ToListAsync(ct);

        var visibleMembers = liveMembers
            .Where(m => reachable.Contains(m.GroupId))
            .Select(m => m.Id)
            .ToHashSet();

        var people = stored.Links
            .Where(l => visibleMembers.Contains(l.MemberId))
            .GroupBy(l => l.PersonId)
            .Select(g => new SharedPersonResponse(
                g.Key,
                g.Select(l => l.MemberId).OrderBy(id => id).ToList()))
            // A pairing that has lost all but one of its members is no longer a pairing.
            .Where(p => p.MemberIds.Count >= 2)
            .OrderBy(p => p.Id)
            .ToList();

        var groupIds = stored.Groups
            .Select(g => g.GroupId)
            .Where(reachable.Contains)
            .OrderBy(id => id)
            .ToList();

        return new SharedLedgerResponse(version, groupIds, people);
    }
}

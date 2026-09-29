using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Paybitch.Api.Features.Auth;
using Paybitch.Api.Features.Avatars;
using Paybitch.Api.Features.Platform.Jobs;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Me;

/// <summary>Result of the anonymize transaction: whether it ran, and the Apple revoke material (step 9).</summary>
public sealed record AnonymizeOutcome(bool Found, byte[]? AppleRefreshTokenEnc, Guid? RevokeJobId, int NewEpoch);

/// <summary>
/// The <c>DELETE /me</c> anonymize transaction (§4.4, D7). Steps 1–8 run in ONE DB transaction, in the
/// documented order; step 9 (Apple revoke) is a post-commit outbox job whose payload — carrying the
/// still-encrypted Apple refresh token — is written inside the transaction (step 7). This keeps network
/// I/O out of the transaction while making the revocation material durable.
/// </summary>
public sealed class AccountAnonymizer(
    AppDbContext db,
    IChangeLogWriter changeLog,
    IClock clock,
    TokenEpochCache epochCache,
    IJobQueue jobs)
{
    private const string DeletedUserName = "Deleted user"; // the user row itself (not a member ghost)
    private const string AppleRevokeJobKind = "apple.revoke";

    public async Task<AnonymizeOutcome> RunAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.UtcNow;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null, ct);
        if (user is null)
            return new AnonymizeOutcome(false, null, null, 0);

        var preScrubEmail = user.Email;
        // E7 (snapshot): capture the user's avatar storage key BEFORE step 3 nulls the column (PII hard delete).
        var preScrubAvatarUrl = user.AvatarUrl;

        // Step 7 (snapshot): capture the encrypted Apple refresh token BEFORE step 7 destroys the row.
        var appleTokenEnc = await db.AuthIdentities
            .Where(a => a.UserId == userId && a.Provider == "apple" && a.AppleRefreshTokenEnc != null)
            .Select(a => a.AppleRefreshTokenEnc)
            .FirstOrDefaultAsync(ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Steps 1–3: soft delete, PII scrub, epoch bump.
        user.DeletedAt = now;
        user.DisplayName = DeletedUserName;
        user.Email = null;
        user.AvatarUrl = null;
        user.Locale = "cs";
        user.TokenEpoch += 1;
        await db.SaveChangesAsync(ct);

        var placeholder = AuthLocalization.NeutralMemberPlaceholder(user.Locale);

        // Capture the user's member rows BEFORE unlinking (invite scrub + change_log both need the ids).
        var memberRows = await db.GroupMembers
            .Where(m => m.UserId == userId)
            .Select(m => new { m.Id, m.GroupId })
            .ToListAsync(ct);
        var memberIds = memberRows.Select(r => r.Id).ToList();

        // E7 (snapshot): every member-image storage key this user's live/left rows point at, captured BEFORE
        // step 5b/6 null the columns (PII hard delete — user_id == userId OR former_user_id == userId).
        var memberImageKeys = await db.GroupMembers
            .Where(m => (m.UserId == userId || m.FormerUserId == userId) && m.ImageUrl != null)
            .Select(m => m.ImageUrl!)
            .ToListAsync(ct);

        // Step 4: scrub the user's email copies in invites (before step 5 nulls user_id).
        await db.Invites
            .Where(i => (preScrubEmail != null && i.Email == preScrubEmail)
                        || (i.MemberId != null && memberIds.Contains(i.MemberId.Value)))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Email, (string?)null), ct);

        // Step 5a: sole-owner succession (runs before the NULL-out so promotion still sees the real row).
        await ApplySoleOwnerRuleAsync(userId, now, ct);

        // Step 5b: unlink every still-linked row → ghost, PII-scrubbed, role demoted to member.
        await db.GroupMembers
            .Where(m => m.UserId == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.UserId, (Guid?)null)
                .SetProperty(m => m.Role, "member")
                .SetProperty(m => m.DisplayName, placeholder)
                .SetProperty(m => m.ImageUrl, (string?)null)
                .SetProperty(m => m.Version, m => m.Version + 1)
                .SetProperty(m => m.UpdatedAt, now), ct);

        foreach (var r in memberRows)
        {
            // The member upsert tells OTHER members' devices the row went ghost (§3.5.2);
            // the access revoke is belt-and-braces for the deleter's own live devices (§4.4 step 5).
            changeLog.Append(r.GroupId, ChangeLogEntityTypes.Member, r.Id, isDelete: false);
            changeLog.AppendAccess(r.GroupId, userId, isRevoke: true);
        }

        // Step 6: erase hook for previously-left rows (their user_id is already NULL; former_user_id is the route).
        var leftRows = await db.GroupMembers
            .Where(m => m.FormerUserId == userId)
            .Select(m => new { m.Id, m.GroupId })
            .ToListAsync(ct);
        if (leftRows.Count > 0)
        {
            await db.GroupMembers
                .Where(m => m.FormerUserId == userId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.DisplayName, placeholder)
                    .SetProperty(m => m.ImageUrl, (string?)null)
                    .SetProperty(m => m.FormerUserId, (Guid?)null)
                    .SetProperty(m => m.Version, m => m.Version + 1)
                    .SetProperty(m => m.UpdatedAt, now), ct);

            foreach (var r in leftRows)
                changeLog.Append(r.GroupId, ChangeLogEntityTypes.Member, r.Id, isDelete: false);
        }

        // Persist the change_log rows appended above.
        await db.SaveChangesAsync(ct);

        // Step 7: hard-delete pure credential/PII material. Break the refresh_tokens.replaced_by
        // self-references first — the FK is ON DELETE RESTRICT, so deleting a rotated chain in one
        // statement would otherwise trip the constraint (Postgres checks RESTRICT immediately).
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.ReplacedBy != null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ReplacedBy, (Guid?)null), ct);

        await db.Devices.Where(d => d.UserId == userId).ExecuteDeleteAsync(ct);
        await db.RefreshTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
        await db.AuthIdentities.Where(a => a.UserId == userId).ExecuteDeleteAsync(ct);

        // Step 7 (outbox): the step-9 Apple revoke job, payload carrying the encrypted token.
        Guid? revokeJobId = null;
        if (appleTokenEnc is not null)
        {
            var job = new Job
            {
                Kind = AppleRevokeJobKind,
                Payload = JsonSerializer.Serialize(new
                {
                    userId = userId.ToString(),
                    appleRefreshTokenEnc = Convert.ToBase64String(appleTokenEnc),
                }),
                DedupeKey = $"{AppleRevokeJobKind}:{userId}",
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync(ct);
            revokeJobId = job.Id;
        }

        // Step 8: scrub the actor on the append-only activity log (the SET NULL that never fires under D7).
        await db.ActivityLog
            .Where(a => a.ActorUser == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ActorUser, (Guid?)null), ct);

        // --- Extension GDPR erasure (E3–E10a): additive steps inside the same D7 transaction. ---

        // E7 avatars (PII → hard delete): two-phase-delete the user avatar and every captured member image
        // key. The E0 helper is a no-op for a null/non-Guid key, so an absent avatar is skipped cleanly.
        await AvatarBlobs.EnqueueDeleteAsync(db, jobs, preScrubAvatarUrl, AvatarBlobs.ReasonGdprErase, ct);
        foreach (var imageKey in memberImageKeys)
            await AvatarBlobs.EnqueueDeleteAsync(db, jobs, imageKey, AvatarBlobs.ReasonGdprErase, ct);

        // E4 exports: an export snapshot is a full copy of the subject's data. Two-phase-delete each artifact
        // blob (object key == row id, EXT-D4a), then hard-delete every export_results row the user owns.
        var exportBlobKeys = await db.ExportResults
            .Where(r => r.RequestedBy == userId && r.StorageKey != null)
            .Select(r => r.Id)
            .ToListAsync(ct);
        foreach (var exportId in exportBlobKeys)
            await AvatarBlobs.EnqueueDeleteAsync(db, jobs, exportId.ToString(), AvatarBlobs.ReasonGdprErase, ct);
        await db.ExportResults.Where(r => r.RequestedBy == userId).ExecuteDeleteAsync(ct);

        // E5 notifications: drop the user's pref overrides and reset the digest opt-in to its default.
        // email_suppressions is a deliverability/compliance record, NOT user PII — it MUST survive.
        await db.NotificationPrefs.Where(p => p.UserId == userId).ExecuteDeleteAsync(ct);
        user.DigestOptIn = false; // opt-in default (User.DigestOptIn)

        // E11 shared ledger: the pooled-group choice and the "who is who" pairings are this account's
        // private opinion about other people's identities — there is nobody left to hold it, and the
        // soft-deleted users row means no FK cascade will fire for us.
        await db.SharedLedgerGroups.Where(g => g.UserId == userId).ExecuteDeleteAsync(ct);
        await db.SharedLedgerLinks.Where(l => l.UserId == userId).ExecuteDeleteAsync(ct);

        // E6 email auth: purge every login/link/verify OTP tied to the user id or the pre-scrub address.
        await db.EmailLoginTokens
            .Where(t => t.UserId == userId || (preScrubEmail != null && t.Email == preScrubEmail))
            .ExecuteDeleteAsync(ct);

        // E10a comments (PII body → scrub IN PLACE, not tombstone — EXT-DC2): live rows keep rendering with the
        // "former member" author, so scrub the body, bump the version, and emit a change_log upsert so peers
        // converge. Already-tombstoned rows get only the at-rest body scrub (no version bump, no change_log).
        var liveComments = await db.Comments
            .Where(c => c.AuthorUser == userId && c.DeletedAt == null)
            .ToListAsync(ct);
        foreach (var comment in liveComments)
        {
            comment.Body = placeholder;
            comment.Version += 1;
            changeLog.Append(comment.GroupId, ChangeLogEntityTypes.Comment, comment.Id, isDelete: false);
        }
        await db.Comments
            .Where(c => c.AuthorUser == userId && c.DeletedAt != null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Body, placeholder), ct);

        // Flush the digest reset, the in-place comment scrubs, and their change_log rows before commit.
        await db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);

        // Publish the bumped epoch so this instance converges immediately (§4.1).
        epochCache.Set(userId, user.TokenEpoch);

        return new AnonymizeOutcome(true, appleTokenEnc, revokeJobId, user.TokenEpoch);
    }

    /// <summary>
    /// §3.8.2 sole-owner succession, applied per group where the deleter is the ONLY active owner:
    /// promote the longest-standing active admin (earliest <c>created_at</c>, tie-break <c>id</c>), else
    /// the longest-standing active linked member; if the deleter is the only linked member, archive the
    /// group. Runs before <c>user_id</c> is nulled. When several owners exist, no promotion fires — the
    /// step-5b demote to <c>member</c> covers it (the §1.3 CHECK forbids a privileged ghost).
    /// </summary>
    private async Task ApplySoleOwnerRuleAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var ownerGroups = await db.GroupMembers
            .Where(m => m.UserId == userId && m.DeletedAt == null && m.Role == "owner")
            .Select(m => m.GroupId)
            .ToListAsync(ct);

        foreach (var groupId in ownerGroups)
        {
            var activeOwners = await db.GroupMembers
                .CountAsync(m => m.GroupId == groupId && m.DeletedAt == null && m.Role == "owner", ct);
            if (activeOwners > 1)
                continue; // several owners — the unlink demote handles it, no succession needed

            var successor =
                await LongestStandingAsync(groupId, userId, "admin", ct)
                ?? await LongestStandingAsync(groupId, userId, "member", ct);

            if (successor is not null)
            {
                successor.Role = "owner";
                successor.Version += 1;
                db.ActivityLog.Add(new ActivityLogEntry
                {
                    GroupId = groupId,
                    ActorUser = userId,
                    Verb = "member.role_changed",
                    TargetType = "member",
                    TargetId = successor.Id,
                    Metadata = JsonSerializer.Serialize(new { reason = "owner_auto_promoted" }),
                });
                await db.SaveChangesAsync(ct);
                changeLog.Append(groupId, ChangeLogEntityTypes.Member, successor.Id, isDelete: false);
            }
            else
            {
                // The deleter is the only linked member left — archive the group instead (§3.8.2).
                var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, ct);
                if (group is not null && group.ArchivedAt is null)
                {
                    group.ArchivedAt = now;
                    group.Version += 1;
                    await db.SaveChangesAsync(ct);
                    changeLog.Append(groupId, ChangeLogEntityTypes.Group, groupId, isDelete: false);
                }
            }
        }
    }

    private Task<GroupMember?> LongestStandingAsync(Guid groupId, Guid userId, string role, CancellationToken ct)
        => db.GroupMembers
            .Where(m => m.GroupId == groupId && m.DeletedAt == null
                        && m.UserId != null && m.UserId != userId && m.Role == role)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .FirstOrDefaultAsync(ct);
}

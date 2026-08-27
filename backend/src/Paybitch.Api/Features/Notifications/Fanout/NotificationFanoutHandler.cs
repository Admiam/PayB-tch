using Microsoft.EntityFrameworkCore;
using Paybitch.Api.Features.Notifications.Email;
using Paybitch.Api.Features.Notifications.Push;
using Paybitch.Api.Features.Platform.Jobs;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Notifications.Fanout;

/// <summary>
/// The <c>notification.fanout</c> worker (EXT-D5e, §5.4/§5.8). Reads <c>activity_log</c> as the single
/// source via the <c>notification_cursor</c> watermark, applies each recipient's resolved prefs
/// (per-group → global → default), coalesces same-group bursts, and dispatches PUSH (via
/// <see cref="IApnsSender"/>) + optional EMAIL (an enqueued <c>email.send</c> job). At-least-once +
/// idempotent (X5): the watermark advances once per run (a mid-run crash re-scans, dropping nothing),
/// duplicate pushes are harmless, and the <c>email.send</c> dedupe key <c>notify:{activityId}:{userId}</c>
/// collapses duplicate emails to exactly one. Nothing user-identifying rides APNs (EXT-D5a) — ids +
/// a name-free <c>loc-key</c> only.
/// </summary>
public sealed class NotificationFanoutHandler(
    AppDbContext db,
    IJobQueue jobQueue,
    IClock clock,
    IServiceProvider services,
    ILoggerFactory loggerFactory) : IJobHandler
{
    private const int BatchSize = 200;
    private const int MaxBatchesPerRun = 25;
    private const int OverlapSeconds = 60;      // bounded re-scan for commit-vs-created_at skew (EXT-D5e)
    private const int CoalesceThreshold = 3;    // ≥3 same-group events collapse to one summary push (§5.7.1)
    private const string ApnsKind = "apns";     // standard push; 'apns_live_activity' is a separate class (EXT-D5g)

    public string Kind => NotificationJobKinds.Fanout;

    public async Task HandleAsync(JobContext job, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger<NotificationFanoutHandler>();

        var cursor = await db.NotificationCursors.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Worker == NotificationWorkers.Fanout, ct);

        // First-ever run: seed the watermark at the newest existing activity and dispatch NOTHING (never
        // blast historical events on cold start). Subsequent runs dispatch only what arrives after.
        if (cursor is null)
        {
            var newest = await db.ActivityLog.AsNoTracking()
                .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
                .Select(a => new { a.Id, a.CreatedAt })
                .FirstOrDefaultAsync(ct);

            await UpsertCursorAsync(newest?.CreatedAt ?? clock.UtcNow, newest?.Id ?? Guid.Empty, ct);
            logger.LogInformation("notification.fanout cursor seeded (no dispatch on cold start)");
            return;
        }

        var apns = services.GetService<IApnsSender>()
                   ?? new LogApnsSender(loggerFactory.CreateLogger<LogApnsSender>());

        var lastCreated = cursor.LastCreatedAt;
        var lastId = cursor.LastActivityId;
        var since = lastCreated.AddSeconds(-OverlapSeconds);
        var dispatched = 0;

        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            var fetchFrom = since;
            var candidates = await db.ActivityLog.AsNoTracking()
                .Where(a => a.CreatedAt >= fetchFrom)
                .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (candidates.Count == 0)
                break;

            // Consistent in-memory order + gate (ordinal on uuidv7 ≈ chronological), independent of the
            // DB's uuid collation so the watermark can never skip or re-count within equal timestamps.
            var ordered = candidates
                .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id.ToString(), StringComparer.Ordinal)
                .ToList();

            var toDispatch = ordered.Where(a => IsAfter(a.CreatedAt, a.Id, lastCreated, lastId)).ToList();
            if (toDispatch.Count == 0)
                break; // only the overlap tail — nothing new

            await DispatchBatchAsync(toDispatch, apns, ct);
            dispatched += toDispatch.Count;

            var high = toDispatch[^1];
            lastCreated = high.CreatedAt;
            lastId = high.Id;
            since = lastCreated;

            if (candidates.Count < BatchSize)
                break; // drained
        }

        // Advance the watermark exactly once, after the batch is dispatched (no-drop on a mid-run crash).
        await UpsertCursorAsync(lastCreated, lastId, ct);
        if (dispatched > 0)
            logger.LogInformation("notification.fanout dispatched {Count} event(s)", dispatched);
    }

    private async Task DispatchBatchAsync(
        IReadOnlyList<ActivityLogEntry> rows, IApnsSender apns, CancellationToken ct)
    {
        var groupIds = rows.Select(r => r.GroupId).Distinct().ToList();

        // Recipients per group (active member users, hydrated for email locale/address).
        var membersByGroup = new Dictionary<Guid, List<Recipient>>();
        foreach (var groupId in groupIds)
        {
            var members = await (
                from m in db.GroupMembers.AsNoTracking()
                where m.GroupId == groupId && m.UserId != null && m.DeletedAt == null
                join u in db.Users.AsNoTracking() on m.UserId equals (Guid?)u.Id
                where u.DeletedAt == null
                select new Recipient(u.Id, u.Email, u.Locale)).ToListAsync(ct);
            membersByGroup[groupId] = members;
        }

        var userIds = membersByGroup.Values.SelectMany(m => m).Select(r => r.UserId).Distinct().ToList();
        if (userIds.Count == 0)
            return;

        // Batch-load prefs + apns tokens for all recipients in the batch (avoids per-row N+1).
        var overridesByUser = (await db.NotificationPrefs.AsNoTracking()
                .Where(p => userIds.Contains(p.UserId)).ToListAsync(ct))
            .GroupBy(p => p.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<NotificationPref>)g.ToList());

        var tokensByUser = (await db.Devices.AsNoTracking()
                .Where(d => userIds.Contains(d.UserId) && d.Kind == ApnsKind)
                .Select(d => new { d.UserId, d.ApnsToken }).ToListAsync(ct))
            .GroupBy(d => d.UserId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ApnsToken).Distinct().ToList());

        var pushPlan = new List<(Guid UserId, Guid GroupId, ActivityLogEntry Row)>();
        var emailPlan = new List<(Recipient Rec, Guid GroupId, ActivityLogEntry Row)>();

        foreach (var row in rows)
        {
            var members = membersByGroup.GetValueOrDefault(row.GroupId);
            if (members is null)
                continue;

            foreach (var rec in members)
            {
                if (rec.UserId == row.ActorUser)
                    continue; // never notify the actor about their own action

                var overrides = overridesByUser.GetValueOrDefault(rec.UserId) ?? [];

                if (NotificationPrefsResolver.IsEnabled(overrides, row.GroupId, row.Verb, NotificationChannels.Push))
                    pushPlan.Add((rec.UserId, row.GroupId, row));

                if (rec.Email is not null &&
                    NotificationPrefsResolver.IsEnabled(overrides, row.GroupId, row.Verb, NotificationChannels.Email))
                    emailPlan.Add((rec, row.GroupId, row));
            }
        }

        await DispatchPushAsync(pushPlan, tokensByUser, apns, ct);
        await EnqueueEmailsAsync(emailPlan, ct);
    }

    private static async Task DispatchPushAsync(
        List<(Guid UserId, Guid GroupId, ActivityLogEntry Row)> plan,
        Dictionary<Guid, List<string>> tokensByUser,
        IApnsSender apns,
        CancellationToken ct)
    {
        foreach (var group in plan.GroupBy(x => (x.UserId, x.GroupId)))
        {
            var tokens = tokensByUser.GetValueOrDefault(group.Key.UserId);
            if (tokens is null || tokens.Count == 0)
                continue;

            var events = group.Select(x => x.Row).DistinctBy(r => r.Id)
                .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id.ToString(), StringComparer.Ordinal)
                .ToList();

            if (events.Count >= CoalesceThreshold)
            {
                // One name-free summary push per device; the NSE renders "N new … in <group>" locally.
                var latest = events[^1];
                foreach (var token in tokens)
                    await apns.SendAsync(
                        new ApnsPush(token, latest.Verb, group.Key.GroupId, null, ApnsLocKeys.NewActivitySummary, events.Count),
                        ct);
            }
            else
            {
                foreach (var row in events)
                    foreach (var token in tokens)
                        await apns.SendAsync(
                            new ApnsPush(token, row.Verb, group.Key.GroupId, row.TargetId, ApnsLocKeys.NewActivity),
                            ct);
            }
        }
    }

    private async Task EnqueueEmailsAsync(
        List<(Recipient Rec, Guid GroupId, ActivityLogEntry Row)> plan, CancellationToken ct)
    {
        foreach (var (rec, groupId, row) in plan)
        {
            if (rec.Email is null)
                continue;

            var template = row.Verb == NotificationEvents.ExportReady
                ? EmailTemplateKinds.ExportReady
                : EmailTemplateKinds.Activity;

            var payload = new EmailSendPayload(
                Template: template,
                To: rec.Email,
                Locale: rec.Locale,
                UserId: rec.UserId,
                GroupId: groupId,
                ActorUserId: row.ActorUser,
                EntityId: row.TargetId,
                Verb: row.Verb);

            // Dedupe key makes a double-fired fan-out enqueue exactly one email (§5.8).
            await jobQueue.EnqueueAsync(
                NotificationJobKinds.EmailSend, payload,
                dedupeKey: $"notify:{row.Id}:{rec.UserId}", runAt: null, ct);
        }
    }

    private async Task UpsertCursorAsync(DateTimeOffset lastCreated, Guid lastId, CancellationToken ct)
    {
        var existing = await db.NotificationCursors
            .FirstOrDefaultAsync(c => c.Worker == NotificationWorkers.Fanout, ct);

        if (existing is null)
        {
            db.NotificationCursors.Add(new NotificationCursor
            {
                Worker = NotificationWorkers.Fanout,
                LastActivityId = lastId,
                LastCreatedAt = lastCreated,
                UpdatedAt = clock.UtcNow,
            });
        }
        else
        {
            existing.LastActivityId = lastId;
            existing.LastCreatedAt = lastCreated;
            existing.UpdatedAt = clock.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    private static bool IsAfter(DateTimeOffset createdAt, Guid id, DateTimeOffset baseCreated, Guid baseId)
        => createdAt > baseCreated
           || (createdAt == baseCreated
               && string.CompareOrdinal(id.ToString(), baseId.ToString()) > 0);

    /// <summary>A fan-out email recipient — a group member user hydrated for send (address + locale).</summary>
    private sealed record Recipient(Guid UserId, string? Email, string Locale);
}

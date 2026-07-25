using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Features.Groups.Support;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Notifications;

/// <summary>
/// The per-user notification preference matrix (E5, §5.4). Caller-scoped account settings — NOT a synced
/// group entity (§5.5), so no <c>change_log</c> row. <c>GET</c> returns the raw overrides, the default
/// matrix, and the resolved effective set; <c>PUT</c> is a full replace guarded by an optional
/// <c>If-Match</c> (last-write-safe across the caller's devices, D8-lite). Writing a per-group override
/// runs a D6 membership check first, so prefs can't probe group existence (§5.7).
/// </summary>
public sealed class NotificationPrefsModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/me/notification-prefs", GetAsync)
            .WithName("GetNotificationPrefs")
            .WithSummary("Get the caller's notification overrides, the default matrix, and the resolved set.")
            .WithTags("Notifications");

        app.MapPut("/me/notification-prefs", PutAsync)
            .WithName("UpdateNotificationPrefs")
            .WithSummary("Full-replace the caller's notification overrides (optional If-Match).")
            .WithTags("Notifications");
    }

    // --- GET /me/notification-prefs ---
    private static async Task<IResult> GetAsync(
        HttpContext http, AppDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var overrides = await LoadOverridesAsync(db, currentUser.UserId, ct);
        var response = BuildResponse(overrides);
        ConcurrencyHeaders.SetETag(http.Response, response.Version);
        return Results.Ok(response);
    }

    // --- PUT /me/notification-prefs ---
    private static async Task<IResult> PutAsync(
        UpdateNotificationPrefsRequest? request,
        HttpContext http,
        AppDbContext db,
        IGroupAccess groupAccess,
        IClock clock,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        var userId = currentUser.UserId;
        var inputs = request?.Prefs ?? [];

        // 1. Shape validation with specific 422 twins (§5.7 over-post guard).
        foreach (var p in inputs)
        {
            if (p.EventType is null || !NotificationEvents.IsValid(p.EventType))
                return Problems.Validation(
                    NotificationProblemCodes.UnknownEventType, "eventType",
                    "eventType must be a known §3.10 activity verb.");
            if (p.Channel is null || !NotificationChannels.IsValid(p.Channel))
                return Problems.Validation(
                    NotificationProblemCodes.InvalidChannel, "channel",
                    "channel must be 'push' or 'email'.");
        }

        // 2. D6: every per-group override targets a group the caller is actually in (404 to non-members).
        var distinctGroups = inputs.Where(p => p.GroupId is not null).Select(p => p.GroupId!.Value).Distinct();
        foreach (var groupId in distinctGroups)
        {
            var membership = await groupAccess.GetMembershipAsync(groupId, userId, ct);
            if (membership is null)
                return Problems.NotFound();
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // 3. Optional If-Match precondition (low-stakes: absent ⇒ last-write-wins, §5.5).
        var current = await LoadOverridesAsync(db, userId, ct);
        if (ConcurrencyHeaders.TryReadIfMatch(http.Request, out var ifMatch))
        {
            var currentVersion = NotificationPrefsResolver.ComputeVersion(current);
            if (ifMatch != currentVersion)
                return Problems.VersionConflict(BuildResponse(current));
        }

        // 4. Full replace: drop the caller's overrides, insert the de-duplicated new set.
        await db.NotificationPrefs.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);

        var deduped = inputs
            .GroupBy(p => (p.GroupId, p.EventType, p.Channel))
            .Select(g => g.Last());

        var now = clock.UtcNow;
        foreach (var p in deduped)
        {
            db.NotificationPrefs.Add(new NotificationPref
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                GroupId = p.GroupId,
                EventType = p.EventType!,   // non-null after the validation loop above
                Channel = p.Channel!,
                Enabled = p.Enabled,
                UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var stored = await LoadOverridesAsync(db, userId, ct);
        var response = BuildResponse(stored);
        ConcurrencyHeaders.SetETag(http.Response, response.Version);
        return Results.Ok(response);
    }

    private static Task<List<NotificationPref>> LoadOverridesAsync(AppDbContext db, Guid userId, CancellationToken ct)
        => db.NotificationPrefs.AsNoTracking()
            .Where(x => x.UserId == userId)
            .ToListAsync(ct);

    /// <summary>Project the stored overrides into the wire shape: version + overrides + defaults + resolved.</summary>
    private static NotificationPrefsResponse BuildResponse(IReadOnlyList<NotificationPref> overrides)
    {
        var version = NotificationPrefsResolver.ComputeVersion(overrides);

        var overrideRows = overrides
            .OrderBy(o => o.GroupId?.ToString() ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(o => o.EventType, StringComparer.Ordinal)
            .ThenBy(o => o.Channel, StringComparer.Ordinal)
            .Select(o => new NotificationPrefRow(o.GroupId, o.EventType, o.Channel, o.Enabled))
            .ToList();

        var defaults = NotificationDefaults.OnPairs
            .Select(p => new DefaultPrefRow(p.Event, p.Channel, true))
            .ToList();

        var resolved = BuildResolved(overrides);

        return new NotificationPrefsResponse(version, overrideRows, defaults, resolved);
    }

    /// <summary>
    /// The effective set for each group the caller has an override in, over the union of the default-on
    /// events and any overridden events, both channels. Bounded and meaningful — resolution for an
    /// un-overridden group is just the default matrix already returned in <c>defaults</c> (§5.4).
    /// </summary>
    private static List<ResolvedPrefRow> BuildResolved(IReadOnlyList<NotificationPref> overrides)
    {
        var groups = overrides.Where(o => o.GroupId is not null).Select(o => o.GroupId!.Value).Distinct().ToList();
        if (groups.Count == 0)
            return [];

        var events = NotificationDefaults.OnPairs.Select(p => p.Event)
            .Concat(overrides.Select(o => o.EventType))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList();

        var rows = new List<ResolvedPrefRow>();
        foreach (var groupId in groups.OrderBy(g => g.ToString(), StringComparer.Ordinal))
        {
            foreach (var eventType in events)
            {
                foreach (var channel in new[] { NotificationChannels.Push, NotificationChannels.Email })
                {
                    var r = NotificationPrefsResolver.Resolve(overrides, groupId, eventType, channel);
                    rows.Add(new ResolvedPrefRow(r.GroupId, r.EventType, r.Channel, r.Enabled, r.Source));
                }
            }
        }

        return rows;
    }
}

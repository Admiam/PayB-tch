using System.Security.Cryptography;
using System.Text;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Notifications;

/// <summary>The provenance of a resolved value (EXT-D5b, §5.4).</summary>
public static class PrefSources
{
    public const string Group = "group";
    public const string Global = "global";
    public const string Default = "default";
}

/// <summary>One resolved (group, event, channel) decision plus where it came from.</summary>
public sealed record ResolvedPref(Guid GroupId, string EventType, string Channel, bool Enabled, string Source);

/// <summary>
/// Pure preference resolution (EXT-D5b): <c>resolve(group, event, channel)</c> = first non-null of
/// {per-group override, global override, default matrix}. No DB, no state — the effective set is always
/// derived from the raw override rows (X2). Also owns the aggregate <c>version</c>/ETag derivation.
/// </summary>
public static class NotificationPrefsResolver
{
    /// <summary>
    /// Resolve a single decision against the caller's raw override rows.
    /// Per-group override wins over the global (group-null) override, which wins over the default matrix.
    /// </summary>
    public static ResolvedPref Resolve(
        IReadOnlyList<NotificationPref> overrides, Guid groupId, string eventType, string channel)
    {
        foreach (var o in overrides)
            if (o.GroupId == groupId && o.EventType == eventType && o.Channel == channel)
                return new ResolvedPref(groupId, eventType, channel, o.Enabled, PrefSources.Group);

        foreach (var o in overrides)
            if (o.GroupId is null && o.EventType == eventType && o.Channel == channel)
                return new ResolvedPref(groupId, eventType, channel, o.Enabled, PrefSources.Global);

        return new ResolvedPref(
            groupId, eventType, channel, NotificationDefaults.IsOn(eventType, channel), PrefSources.Default);
    }

    /// <summary>Just the boolean decision (fan-out hot path).</summary>
    public static bool IsEnabled(
        IReadOnlyList<NotificationPref> overrides, Guid groupId, string eventType, string channel)
        => Resolve(overrides, groupId, eventType, channel).Enabled;

    /// <summary>
    /// A content-addressed aggregate version for the caller's override SET (§5.5). There is no stored
    /// version column (the table is pure account-settings deltas), so the ETag is a stable, order-
    /// independent hash of the normalized rows: identical content ⇒ identical version (idempotent PUT
    /// bumps once, §5.8), any change ⇒ a different version (last-write-safe <c>If-Match</c> across the
    /// user's devices). Non-negative Int32 for a clean <c>ETag: "&lt;n&gt;"</c>.
    /// </summary>
    public static int ComputeVersion(IReadOnlyList<NotificationPref> overrides)
    {
        if (overrides.Count == 0)
            return 0;

        var normalized = overrides
            .Select(o => $"{o.GroupId?.ToString() ?? "*"}|{o.EventType}|{o.Channel}|{(o.Enabled ? 1 : 0)}")
            .OrderBy(s => s, StringComparer.Ordinal);

        var joined = string.Join("\n", normalized);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));

        // First 4 bytes as an unsigned int, masked into the non-negative Int32 range.
        var raw = BitConverter.ToUInt32(hash, 0);
        return (int)(raw & 0x7FFFFFFF);
    }
}

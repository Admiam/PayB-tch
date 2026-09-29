namespace Paybitch.Infrastructure.Entities;

/// <summary>
/// shared_ledger_groups — one row per group the user has opted into their combined "shared debt" view.
/// </summary>
/// <remarks>
/// Caller-scoped account settings, like <see cref="NotificationPref"/>: a private view configuration,
/// not a synced group entity, so it writes no <c>change_log</c> row and nobody else in the group can
/// see or be affected by it. Opt-in is explicit rather than inferred from the presence of links,
/// because "which of my groups are pooled together" is a decision with money on the screen — it should
/// never change because a pairing was added somewhere else.
/// </remarks>
public sealed class SharedLedgerGroup
{
    public Guid UserId { get; set; }
    public Guid GroupId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

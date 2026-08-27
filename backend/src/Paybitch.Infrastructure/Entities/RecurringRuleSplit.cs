namespace Paybitch.Infrastructure.Entities;

/// <summary>
/// E3 recurring_rule_splits — the split TEMPLATE. No rows ⇒ dynamic-equal over active members at
/// fire time; rows present ⇒ pinned set. Composite PK (rule_id, group_member_id).
/// </summary>
public sealed class RecurringRuleSplit
{
    public Guid RuleId { get; set; }
    public Guid GroupMemberId { get; set; }
    public int? Weight { get; set; }                           // shares template input (audit)
    public int? BasisPoints { get; set; }                      // percentage template input (audit)
    public long? AmountMinor { get; set; }                     // exact template input (fixed per-occurrence share)
}

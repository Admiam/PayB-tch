namespace Paybitch.Infrastructure.Entities;

/// <summary>
/// expense_splits — the single source of participants (D3). Composite PK (expense_id, group_member_id).
/// share_minor is the resolved owed amount; weight / basis_points are the audited inputs.
/// </summary>
public sealed class ExpenseSplit
{
    public Guid ExpenseId { get; set; }
    public Guid GroupMemberId { get; set; }
    public long ShareMinor { get; set; }                        // resolved owed amount (>= 0)
    public int? Weight { get; set; }                            // shares-mode input (audit)
    public int? BasisPoints { get; set; }                       // percentage-mode input (audit)
}

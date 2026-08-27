namespace Paybitch.Infrastructure.Entities;

/// <summary>
/// expenses. Money is stored as PRIMITIVE columns (<see cref="AmountMinor"/> + <see cref="Currency"/>);
/// handlers build Domain.Money at the edge. No EF value converter.
/// </summary>
public sealed class Expense : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid GroupId { get; set; }
    public string? ClientId { get; set; }                       // offline id → (group_id, client_id) idempotency (D9)
    public string Title { get; set; } = string.Empty;
    public long AmountMinor { get; set; }                       // integer minor units (D1)
    public string Currency { get; set; } = string.Empty;        // FK currencies.code
    public Guid PaidBy { get; set; }                            // FK group_members.id
    public string SplitType { get; set; } = string.Empty;       // 'equal' | 'exact' | 'shares' | 'percentage'
    public Guid? CategoryId { get; set; }
    public string? IconSymbol { get; set; }
    public DateOnly ExpenseDate { get; set; }
    public string? Notes { get; set; }
    public int Version { get; set; } = 1;
    public Guid? CreatedBy { get; set; }
    public Guid? RecurringRuleId { get; set; }                  // E3 link back to originating rule
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
}

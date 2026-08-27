namespace Paybitch.Infrastructure.Entities;

/// <summary>
/// E3 recurring_rules — a TEMPLATE. The ledger truth is the ordinary expenses it fires (X2).
/// Curated typed recurrence columns, not an RRULE string.
/// </summary>
public sealed class RecurringRule : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid GroupId { get; set; }
    public string? ClientId { get; set; }                       // (group_id, client_id) idempotency (D9)
    public string Title { get; set; } = string.Empty;
    public long AmountMinor { get; set; }                       // integer minor units (D1)
    public string Currency { get; set; } = string.Empty;        // FK currencies.code
    public Guid PaidBy { get; set; }                            // FK group_members.id
    public string SplitType { get; set; } = string.Empty;
    public Guid? CategoryId { get; set; }
    public string? IconSymbol { get; set; }
    public string? Notes { get; set; }
    public string Freq { get; set; } = string.Empty;           // 'weekly' | 'monthly' | 'yearly'
    public int Interval { get; set; } = 1;                     // 1..60
    public int? ByMonthDay { get; set; }                       // monthly only, 1..31
    public int? ByWeekday { get; set; }                        // weekly only, ISO 1=Mon..7=Sun
    public string Timezone { get; set; } = string.Empty;       // IANA tzdb id
    public DateOnly StartsOn { get; set; }
    public DateOnly? EndsOn { get; set; }                      // inclusive; xor with remaining_count
    public int? RemainingCount { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }             // NULL iff status='ended'
    public DateTimeOffset? LastRunAt { get; set; }
    public string Status { get; set; } = "active";            // 'active' | 'paused' | 'ended'
    public string? PauseReason { get; set; }
    public int Version { get; set; } = 1;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
}

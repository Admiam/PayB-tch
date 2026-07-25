namespace Paybitch.Infrastructure.Entities;

/// <summary>change_log_watermark — single-row table holding the highest pruned seq (/sync 410 boundary).</summary>
public sealed class ChangeLogWatermark
{
    public bool One { get; set; } = true;                     // single-row PK, CHECK (one)
    public long PrunedThroughSeq { get; set; }
    public DateTimeOffset? PrunedAt { get; set; }
}

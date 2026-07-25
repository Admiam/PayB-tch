using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class ChangeLogWatermarkConfiguration : IEntityTypeConfiguration<ChangeLogWatermark>
{
    public void Configure(EntityTypeBuilder<ChangeLogWatermark> b)
    {
        b.ToTable("change_log_watermark", t =>
            t.HasCheckConstraint("ck_change_log_watermark_one", "one"));

        b.HasKey(x => x.One);
        b.Property(x => x.One).ValueGeneratedNever().HasDefaultValue(true);
        b.Property(x => x.PrunedThroughSeq).HasDefaultValue(0L);

        // Single-row seed so /sync's 410 boundary has a watermark from day one.
        b.HasData(new ChangeLogWatermark { One = true, PrunedThroughSeq = 0, PrunedAt = null });
    }
}

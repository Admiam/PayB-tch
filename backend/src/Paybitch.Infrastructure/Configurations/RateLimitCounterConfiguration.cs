using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class RateLimitCounterConfiguration : IEntityTypeConfiguration<RateLimitCounter>
{
    public void Configure(EntityTypeBuilder<RateLimitCounter> b)
    {
        b.ToTable("rate_limit_counters");

        b.HasKey(x => new { x.PartitionHash, x.WindowStart });
        b.Property(x => x.Count).HasDefaultValue(0);

        b.HasIndex(x => x.WindowStart).HasDatabaseName("ix_rate_limit_gc");
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class ActivityLogEntryConfiguration : IEntityTypeConfiguration<ActivityLogEntry>
{
    public void Configure(EntityTypeBuilder<ActivityLogEntry> b)
    {
        b.ToTable("activity_log");

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Verb).IsRequired();
        b.Property(x => x.TargetType).IsRequired();
        b.Property(x => x.Metadata).HasColumnType("jsonb");
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

        b.HasOne<Group>().WithMany()
            .HasForeignKey(x => x.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne<User>().WithMany()
            .HasForeignKey(x => x.ActorUser)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasIndex(x => new { x.GroupId, x.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_activity_group_time");
    }
}

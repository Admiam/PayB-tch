using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class NotificationPrefConfiguration : IEntityTypeConfiguration<NotificationPref>
{
    public void Configure(EntityTypeBuilder<NotificationPref> b)
    {
        b.ToTable("notification_prefs", t =>
            t.HasCheckConstraint("ck_notification_prefs_channel", "channel IN ('push','email')"));

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.EventType).IsRequired();
        b.Property(x => x.Channel).IsRequired();
        b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        b.HasOne<User>().WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne<Group>().WithMany()
            .HasForeignKey(x => x.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        // One override row per (user, scope, event, channel) — partial uniques instead of a sentinel.
        b.HasIndex(x => new { x.UserId, x.EventType, x.Channel })
            .IsUnique()
            .HasDatabaseName("uq_notif_prefs_global")
            .HasFilter("group_id IS NULL");

        b.HasIndex(x => new { x.UserId, x.GroupId, x.EventType, x.Channel })
            .IsUnique()
            .HasDatabaseName("uq_notif_prefs_group")
            .HasFilter("group_id IS NOT NULL");

        b.HasIndex(x => x.UserId).HasDatabaseName("ix_notif_prefs_user");
    }
}

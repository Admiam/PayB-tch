using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class RecurringRuleConfiguration : IEntityTypeConfiguration<RecurringRule>
{
    public void Configure(EntityTypeBuilder<RecurringRule> b)
    {
        b.ToTable("recurring_rules", t =>
        {
            t.HasCheckConstraint("ck_recurring_rules_title_length", "length(title) BETWEEN 1 AND 140");
            t.HasCheckConstraint("ck_recurring_rules_amount_positive", "amount_minor > 0");
            t.HasCheckConstraint("ck_recurring_rules_notes_length", "notes IS NULL OR length(notes) <= 2000");
            t.HasCheckConstraint("ck_recurring_rules_freq", "freq IN ('weekly','monthly','yearly')");
            t.HasCheckConstraint("ck_recurring_rules_interval", "\"interval\" BETWEEN 1 AND 60");
            t.HasCheckConstraint("ck_recurring_rules_by_month_day", "by_month_day IS NULL OR by_month_day BETWEEN 1 AND 31");
            t.HasCheckConstraint("ck_recurring_rules_by_weekday", "by_weekday IS NULL OR by_weekday BETWEEN 1 AND 7");
            t.HasCheckConstraint("ck_recurring_rules_remaining_count", "remaining_count IS NULL OR remaining_count >= 0");
            t.HasCheckConstraint("ck_recurring_rules_status", "status IN ('active','paused','ended')");
            t.HasCheckConstraint("ck_recurring_rules_pause_reason",
                "pause_reason IN ('user','member_removed','payer_removed','backlog_exceeded','group_archived')");
            // recurrence shape guards
            t.HasCheckConstraint("ck_recurring_rules_weekly_shape",
                "freq <> 'weekly' OR (by_weekday IS NOT NULL AND by_month_day IS NULL)");
            t.HasCheckConstraint("ck_recurring_rules_monthly_shape",
                "freq <> 'monthly' OR (by_month_day IS NOT NULL AND by_weekday IS NULL)");
            t.HasCheckConstraint("ck_recurring_rules_yearly_shape",
                "freq <> 'yearly' OR (by_weekday IS NULL AND by_month_day IS NULL)");
            t.HasCheckConstraint("ck_recurring_rules_end_bound_xor",
                "NOT (ends_on IS NOT NULL AND remaining_count IS NOT NULL)");
            t.HasCheckConstraint("ck_recurring_rules_ends_after_starts",
                "ends_on IS NULL OR ends_on >= starts_on");
            t.HasCheckConstraint("ck_recurring_rules_ended_next_run",
                "(status = 'ended') = (next_run_at IS NULL)");
        });

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Title).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
        b.Property(x => x.Freq).IsRequired();
        b.Property(x => x.Interval).HasDefaultValue(1);
        b.Property(x => x.Timezone).IsRequired();
        b.Property(x => x.Status).HasDefaultValue("active");
        b.Property(x => x.Version).HasDefaultValue(1);
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        b.HasOne<Group>().WithMany()
            .HasForeignKey(x => x.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne<Currency>().WithMany()
            .HasForeignKey(x => x.Currency)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne<GroupMember>().WithMany()
            .HasForeignKey(x => x.PaidBy)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne<Category>().WithMany()
            .HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasOne<User>().WithMany()
            .HasForeignKey(x => x.CreatedBy)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasIndex(x => new { x.GroupId, x.ClientId })
            .IsUnique()
            .HasDatabaseName("uq_recurring_rules_group_client");

        b.HasIndex(x => x.NextRunAt)
            .HasDatabaseName("ix_recurring_due")
            .HasFilter("status = 'active' AND deleted_at IS NULL");

        b.HasIndex(x => x.GroupId)
            .HasDatabaseName("ix_recurring_group")
            .HasFilter("deleted_at IS NULL");
    }
}

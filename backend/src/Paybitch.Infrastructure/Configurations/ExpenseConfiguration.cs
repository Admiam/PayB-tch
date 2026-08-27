using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class ExpenseConfiguration : IEntityTypeConfiguration<Expense>
{
    public void Configure(EntityTypeBuilder<Expense> b)
    {
        b.ToTable("expenses", t =>
        {
            t.HasCheckConstraint("ck_expenses_title_length", "length(title) BETWEEN 1 AND 140");
            t.HasCheckConstraint("ck_expenses_amount_positive", "amount_minor > 0");
            t.HasCheckConstraint("ck_expenses_split_type",
                "split_type IN ('equal','exact','shares','percentage')");
            t.HasCheckConstraint("ck_expenses_notes_length", "notes IS NULL OR length(notes) <= 2000");
            // NOTE: the DDL upper bound `expense_date <= now()::date + 2` uses now() and is NOT an
            // immutable CHECK expression; the +2d window is enforced by the §3.4 boundary twin
            // (date_out_of_range). Only the immutable lower bound is a DB constraint here.
            t.HasCheckConstraint("ck_expenses_date_min", "expense_date >= DATE '2000-01-01'");
        });

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Title).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
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

        // E3: link back to the originating recurring rule.
        b.HasOne<RecurringRule>().WithMany()
            .HasForeignKey(x => x.RecurringRuleId)
            .OnDelete(DeleteBehavior.SetNull);

        // Per-group create idempotency (D9).
        b.HasIndex(x => new { x.GroupId, x.ClientId })
            .IsUnique()
            .HasDatabaseName("uq_expenses_group_client");

        b.HasIndex(x => new { x.GroupId, x.ExpenseDate, x.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_expenses_group_date")
            .HasFilter("deleted_at IS NULL");

        b.HasIndex(x => x.PaidBy).HasDatabaseName("ix_expenses_paid_by");

        b.HasIndex(x => x.RecurringRuleId)
            .HasDatabaseName("ix_expenses_recurring")
            .HasFilter("recurring_rule_id IS NOT NULL");
    }
}

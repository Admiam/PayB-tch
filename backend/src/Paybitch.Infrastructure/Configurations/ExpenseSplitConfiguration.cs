using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class ExpenseSplitConfiguration : IEntityTypeConfiguration<ExpenseSplit>
{
    public void Configure(EntityTypeBuilder<ExpenseSplit> b)
    {
        b.ToTable("expense_splits", t =>
            t.HasCheckConstraint("ck_expense_splits_share_non_negative", "share_minor >= 0"));

        b.HasKey(x => new { x.ExpenseId, x.GroupMemberId });

        b.HasOne<Expense>().WithMany()
            .HasForeignKey(x => x.ExpenseId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne<GroupMember>().WithMany()
            .HasForeignKey(x => x.GroupMemberId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.GroupMemberId).HasDatabaseName("ix_splits_member");

        // NOTE (migration): SUM(share_minor) = amount_minor per expense is a DEFERRABLE INITIALLY
        // DEFERRED constraint trigger (§1.5) — not expressible in EF; add it in the migration and
        // enforce in handlers.
    }
}

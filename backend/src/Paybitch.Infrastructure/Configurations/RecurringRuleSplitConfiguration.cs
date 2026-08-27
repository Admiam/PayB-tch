using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class RecurringRuleSplitConfiguration : IEntityTypeConfiguration<RecurringRuleSplit>
{
    public void Configure(EntityTypeBuilder<RecurringRuleSplit> b)
    {
        b.ToTable("recurring_rule_splits");

        b.HasKey(x => new { x.RuleId, x.GroupMemberId });

        b.HasOne<RecurringRule>().WithMany()
            .HasForeignKey(x => x.RuleId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne<GroupMember>().WithMany()
            .HasForeignKey(x => x.GroupMemberId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

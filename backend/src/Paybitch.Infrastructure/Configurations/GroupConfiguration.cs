using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class GroupConfiguration : IEntityTypeConfiguration<Group>
{
    public void Configure(EntityTypeBuilder<Group> b)
    {
        b.ToTable("groups", t =>
            t.HasCheckConstraint("ck_groups_name_length", "length(name) BETWEEN 1 AND 100"));

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.DefaultCurrency).HasMaxLength(3).IsFixedLength();
        b.Property(x => x.Version).HasDefaultValue(1);
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        b.HasOne<Currency>().WithMany()
            .HasForeignKey(x => x.DefaultCurrency)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne<User>().WithMany()
            .HasForeignKey(x => x.CreatedBy)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

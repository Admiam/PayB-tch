using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;
using Paybitch.Infrastructure.Seed;

namespace Paybitch.Infrastructure.Configurations;

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> b)
    {
        b.ToTable("categories");

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        b.HasOne<Group>().WithMany()
            .HasForeignKey(x => x.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        // NOTE (migration): the DDL indexes these on lower(name) for case-insensitive uniqueness
        // (uq_categories_global / uq_categories_group). EF's fluent API cannot express a function
        // (expression) index — the generated migration must be edited to use lower(name).
        b.HasIndex(x => x.Name)
            .IsUnique()
            .HasDatabaseName("uq_categories_global")
            .HasFilter("group_id IS NULL AND deleted_at IS NULL");

        b.HasIndex(x => new { x.GroupId, x.Name })
            .IsUnique()
            .HasDatabaseName("uq_categories_group")
            .HasFilter("group_id IS NOT NULL AND deleted_at IS NULL");

        b.HasData(CurrencySeeder.GlobalCategories);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class CommentConfiguration : IEntityTypeConfiguration<Comment>
{
    public void Configure(EntityTypeBuilder<Comment> b)
    {
        b.ToTable("comments", t =>
            t.HasCheckConstraint("ck_comments_body_length", "length(body) BETWEEN 1 AND 2000"));

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Body).IsRequired();
        b.Property(x => x.Version).HasDefaultValue(1);
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        b.HasOne<Group>().WithMany()
            .HasForeignKey(x => x.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne<Expense>().WithMany()
            .HasForeignKey(x => x.ExpenseId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne<User>().WithMany()
            .HasForeignKey(x => x.AuthorUser)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.GroupId, x.ClientId })
            .IsUnique()
            .HasDatabaseName("uq_comments_group_client");

        b.HasIndex(x => new { x.ExpenseId, x.CreatedAt, x.Id })
            .HasDatabaseName("ix_comments_expense")
            .HasFilter("deleted_at IS NULL");

        b.HasIndex(x => x.AuthorUser).HasDatabaseName("ix_comments_author");
    }
}

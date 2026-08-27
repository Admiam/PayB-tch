using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable("refresh_tokens");

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.TokenHash).IsRequired();
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

        b.HasOne<User>().WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne<RefreshToken>().WithMany()
            .HasForeignKey(x => x.ReplacedBy)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.TokenHash)
            .IsUnique()
            .HasDatabaseName("uq_refresh_tokens_token_hash");

        b.HasIndex(x => x.UserId)
            .HasDatabaseName("ix_refresh_user")
            .HasFilter("revoked_at IS NULL");
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class EmailLoginTokenConfiguration : IEntityTypeConfiguration<EmailLoginToken>
{
    public void Configure(EntityTypeBuilder<EmailLoginToken> b)
    {
        b.ToTable("email_login_tokens", t =>
            t.HasCheckConstraint("ck_email_login_tokens_purpose",
                "purpose IN ('login','link','verify_change')"));

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Email).HasColumnType("citext");
        b.Property(x => x.CodeHash).IsRequired();
        b.Property(x => x.Purpose).IsRequired();
        b.Property(x => x.Attempts).HasDefaultValue(0);
        b.Property(x => x.CreatedIp).HasColumnType("inet");
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

        b.HasOne<User>().WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.Email, x.Purpose, x.CreatedAt })
            .IsDescending(false, false, true)
            .HasDatabaseName("ix_email_tokens_lookup")
            .HasFilter("consumed_at IS NULL");

        b.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_email_tokens_expiry");
    }
}

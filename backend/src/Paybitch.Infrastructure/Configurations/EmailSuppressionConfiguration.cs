using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class EmailSuppressionConfiguration : IEntityTypeConfiguration<EmailSuppression>
{
    public void Configure(EntityTypeBuilder<EmailSuppression> b)
    {
        b.ToTable("email_suppressions", t =>
            t.HasCheckConstraint("ck_email_suppressions_reason",
                "reason IN ('bounce','complaint','manual')"));

        b.HasKey(x => x.Email);
        b.Property(x => x.Email).HasColumnType("citext");
        b.Property(x => x.Reason).IsRequired();
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
    }
}

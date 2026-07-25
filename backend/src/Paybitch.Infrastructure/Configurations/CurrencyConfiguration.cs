using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;
using Paybitch.Infrastructure.Seed;

namespace Paybitch.Infrastructure.Configurations;

public sealed class CurrencyConfiguration : IEntityTypeConfiguration<Currency>
{
    public void Configure(EntityTypeBuilder<Currency> b)
    {
        b.ToTable("currencies", t =>
            t.HasCheckConstraint("ck_currencies_minor_units", "minor_units BETWEEN 0 AND 4"));

        b.HasKey(x => x.Code);
        b.Property(x => x.Code).HasMaxLength(3).IsFixedLength();
        b.Property(x => x.MinorUnits).HasColumnType("smallint");
        b.Property(x => x.Symbol).IsRequired();

        b.HasData(CurrencySeeder.Currencies);
    }
}

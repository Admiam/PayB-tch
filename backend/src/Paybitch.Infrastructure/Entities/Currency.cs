namespace Paybitch.Infrastructure.Entities;

/// <summary>currencies — the per-currency scale authority (D2). Seeded, immutable at runtime.</summary>
public sealed class Currency
{
    public string Code { get; set; } = string.Empty;   // char(3) PK
    public int MinorUnits { get; set; }                 // smallint 0..4 (CZK=0, EUR/USD/GBP=2)
    public string Symbol { get; set; } = string.Empty;
}

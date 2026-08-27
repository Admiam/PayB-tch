using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Seed;

/// <summary>
/// HasData seed for the immutable scale authority (D2) and the fixed-UUID global preset categories
/// (§3.9). Both are applied via the entity configurations so a migration ships them.
/// </summary>
public static class CurrencySeeder
{
    /// <summary>v1 closed set: CZK (0 decimals), EUR/USD/GBP (2 decimals).</summary>
    public static readonly Currency[] Currencies =
    [
        new() { Code = "CZK", MinorUnits = 0, Symbol = "Kč" },
        new() { Code = "EUR", MinorUnits = 2, Symbol = "€" },
        new() { Code = "USD", MinorUnits = 2, Symbol = "$" },
        new() { Code = "GBP", MinorUnits = 2, Symbol = "£" },
    ];

    // Fixed timestamp so HasData is deterministic across migration diffs.
    private static readonly DateTimeOffset SeedAt = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Global preset categories (group_id IS NULL) with FIXED UUIDs so clients localize by id.
    /// Names are canonical English slugs; icon_symbol values are SF Symbol names (§3.9).
    /// </summary>
    public static readonly Category[] GlobalCategories =
    [
        Preset("11110000-0000-0000-0000-000000000001", "groceries",     "cart.fill"),
        Preset("11110000-0000-0000-0000-000000000002", "dining",        "fork.knife"),
        Preset("11110000-0000-0000-0000-000000000003", "transport",     "car.fill"),
        Preset("11110000-0000-0000-0000-000000000004", "housing",       "house.fill"),
        Preset("11110000-0000-0000-0000-000000000005", "utilities",     "bolt.fill"),
        Preset("11110000-0000-0000-0000-000000000006", "entertainment", "popcorn.fill"),
        Preset("11110000-0000-0000-0000-000000000007", "travel",        "airplane"),
        Preset("11110000-0000-0000-0000-000000000008", "health",        "cross.case.fill"),
        Preset("11110000-0000-0000-0000-000000000009", "shopping",      "bag.fill"),
        Preset("11110000-0000-0000-0000-000000000010", "other",         "ellipsis.circle.fill"),
    ];

    private static Category Preset(string id, string name, string icon) => new()
    {
        Id = Guid.Parse(id),
        GroupId = null,
        Name = name,
        IconSymbol = icon,
        CreatedAt = SeedAt,
        UpdatedAt = SeedAt,
        DeletedAt = null,
    };
}

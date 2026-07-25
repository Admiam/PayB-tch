namespace Paybitch.Api.Features.Auth;

/// <summary>
/// The two server-authored display strings the auth/GDPR slice must localize (§4.1 precedence tier 3,
/// §4.4 steps 5–6). The client localizes almost everything by id; these are the exceptions the server
/// itself writes into a <c>NOT NULL display_name</c>. Czech is the product's primary locale (D-default);
/// any other locale falls back to English.
/// </summary>
public static class AuthLocalization
{
    /// <summary>Tier-3 provisioning fallback name when Apple gives neither <c>fullName</c> nor an email (§4.1).</summary>
    public static string DefaultDisplayName(string? locale)
        => IsCzech(locale) ? "Nový uživatel" : "New user";

    /// <summary>The neutral placeholder a member row's name is scrubbed to on GDPR erasure (§4.4 steps 5–6).</summary>
    public static string NeutralMemberPlaceholder(string? locale)
        => IsCzech(locale) ? "Bývalý uživatel" : "Former member";

    private static bool IsCzech(string? locale)
        => locale is not null && locale.StartsWith("cs", StringComparison.OrdinalIgnoreCase);
}

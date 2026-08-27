namespace Paybitch.Api.Features.EmailAuth;

/// <summary>
/// Email boundary helpers (§6.2): the single normalization used everywhere an address becomes an
/// identity <c>subject</c> or a token lookup key, the Apple-relay rejection (EXT-D6c), and the
/// display-masking for <c>GET /me/identities</c> (never echoes a full address).
/// </summary>
internal static class EmailNormalization
{
    // Apple "Hide My Email" / private-relay forwarders — never a mailbox we can prove control of, so
    // never a valid email-provider subject (EXT-D6c). Matched on the domain suffix, case-insensitively.
    private static readonly string[] RelayDomainSuffixes = ["privaterelay.appleid.com"];

    /// <summary>Trim + lower-case: the canonical form stored as the identity <c>subject</c> and token <c>email</c>.</summary>
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();

    /// <summary>True when the (already-normalized) address is an Apple private-relay forwarder (EXT-D6c).</summary>
    public static bool IsAppleRelay(string normalizedEmail)
    {
        var at = normalizedEmail.LastIndexOf('@');
        if (at < 0 || at == normalizedEmail.Length - 1)
            return false;
        var domain = normalizedEmail[(at + 1)..];
        return RelayDomainSuffixes.Any(suffix =>
            domain == suffix || domain.EndsWith("." + suffix, StringComparison.Ordinal));
    }

    /// <summary>
    /// A display-safe mask for a linked identity <c>subject</c> (§6.4 <c>GET /me/identities</c>): an email
    /// keeps its first local char + domain (<c>a***@example.com</c>); any other subject (an Apple <c>sub</c>)
    /// is reduced to a short prefix. Never returns the full value.
    /// </summary>
    public static string MaskSubject(string subject)
    {
        var at = subject.IndexOf('@');
        if (at > 0)
        {
            var local = subject[..at];
            var domain = subject[at..]; // includes '@'
            var head = local.Length <= 1 ? local : local[..1];
            return head + "***" + domain;
        }

        return subject.Length <= 4 ? "***" : subject[..4] + "…";
    }
}

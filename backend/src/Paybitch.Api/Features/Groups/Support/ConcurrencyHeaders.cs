using System.Globalization;

namespace Paybitch.Api.Features.Groups.Support;

/// <summary>
/// <c>ETag</c> / <c>If-Match</c> plumbing for the version-guarded aggregates in this slice (§3.4, D8).
/// The ETag is the aggregate <c>version</c> as a strong tag (<c>"7"</c>); <c>If-Match</c> is the
/// client's last-seen version. Absent where required ⇒ <c>428</c>; stale ⇒ <c>412</c> (handled by the
/// caller with the current representation to merge against).
/// </summary>
public static class ConcurrencyHeaders
{
    /// <summary>Write <c>ETag: "&lt;version&gt;"</c> on a successful mutable-aggregate response.</summary>
    public static void SetETag(HttpResponse response, int version)
        => response.Headers.ETag = $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>
    /// Read the strong <c>If-Match</c> version. Returns <c>false</c> when the header is absent or
    /// malformed; a weak validator (<c>W/"…"</c>) is rejected (money mutations demand a strong match).
    /// </summary>
    public static bool TryReadIfMatch(HttpRequest request, out int version)
    {
        version = 0;
        var raw = request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        raw = raw.Trim();
        if (raw.StartsWith("W/", StringComparison.Ordinal))
            return false; // weak validator — not acceptable for a version precondition

        raw = raw.Trim('"');
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out version);
    }
}

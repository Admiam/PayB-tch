using System.Security.Cryptography;
using System.Text;

namespace Paybitch.Api.Features.Notifications;

/// <summary>The decoded, verified payload of an unsubscribe token.</summary>
public sealed record UnsubscribePayload(Guid UserId, string DigestType);

/// <summary>
/// Self-verifying one-click unsubscribe tokens (EXT-D5h, §5.7). The token is
/// <c>base64url(payload) "." base64url(HMAC-SHA256(secret, payload))</c> over
/// <c>{userId}|{digestType}</c> — no server-side row, no expiry, purpose-scoped. Forging an unsubscribe
/// for another user is infeasible without the secret; and even a valid/forged token can only flip
/// <c>users.digest_opt_in</c> (never write <c>email_suppressions</c>), so it can never silence a
/// victim's transactional mail (EXT-D5i).
/// </summary>
public static class UnsubscribeToken
{
    public const string DigestPurpose = "digest";

    public static string Issue(NotificationSecrets secrets, Guid userId, string digestType = DigestPurpose)
    {
        var payload = Encoding.UTF8.GetBytes($"{userId:D}|{digestType}");
        var sig = secrets.SignUnsubscribe(payload);
        return $"{Base64UrlEncode(payload)}.{Base64UrlEncode(sig)}";
    }

    /// <summary>Constant-time verify. Returns null on any malformed / tampered token (uniform reject, X6).</summary>
    public static UnsubscribePayload? Verify(NotificationSecrets secrets, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1)
            return null;

        byte[] payload, sig;
        try
        {
            payload = Base64UrlDecode(token[..dot]);
            sig = Base64UrlDecode(token[(dot + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }

        var expected = secrets.SignUnsubscribe(payload);
        if (!CryptographicOperations.FixedTimeEquals(sig, expected))
            return null;

        var text = Encoding.UTF8.GetString(payload);
        var parts = text.Split('|', 2);
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var userId))
            return null;

        return new UnsubscribePayload(userId, parts[1]);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        var pad = s.Length % 4;
        if (pad == 2) s += "==";
        else if (pad == 3) s += "=";
        else if (pad == 1) throw new FormatException("Invalid base64url length.");
        return Convert.FromBase64String(s);
    }
}

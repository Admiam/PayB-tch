using System.Security.Cryptography;
using System.Text;

namespace Paybitch.Api.Features.Groups.Support;

/// <summary>
/// Single-use invite tokens (§3.8.1, §3.12 link-only v1). The raw token is a 128-bit random value,
/// base64url-encoded, returned to the creator EXACTLY once. Only its SHA-256 hash is persisted
/// (<c>invites.token_hash</c>, UNIQUE), so a leaked DB never yields a usable token; preview/accept
/// re-hash the presented token to look the row up. No expiry/identity is encoded in the token — the
/// row is the authority.
/// </summary>
public static class InviteTokens
{
    private const int TokenBytes = 16; // 128-bit

    /// <summary>Mint a fresh opaque token string (never stored; shown once).</summary>
    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        return Base64UrlEncode(bytes);
    }

    /// <summary>The stable lookup hash of a token string (SHA-256 of its UTF-8 bytes).</summary>
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

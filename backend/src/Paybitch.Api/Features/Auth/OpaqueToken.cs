using System.Security.Cryptography;

namespace Paybitch.Api.Features.Auth;

/// <summary>
/// The opaque refresh token (§4.1): a 256-bit CSPRNG secret handed to the client, of which the server
/// keeps only the SHA-256 hash (<c>refresh_tokens.token_hash</c>, UNIQUE). Opaque ⇒ nothing about the
/// user or family is derivable from the token itself; the hash column is the sole lookup key.
/// </summary>
public static class OpaqueToken
{
    private const int SecretBytes = 32; // 256-bit

    /// <summary>Mint a new (plaintext, hash) pair. The plaintext leaves the process once, in the response.</summary>
    public static (string Token, byte[] Hash) Generate()
    {
        var raw = RandomNumberGenerator.GetBytes(SecretBytes);
        var token = Base64UrlEncode(raw);
        return (token, Hash(token));
    }

    /// <summary>SHA-256 of the presented token, for the constant-shape lookup against <c>token_hash</c>.</summary>
    public static byte[] Hash(string token)
        => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

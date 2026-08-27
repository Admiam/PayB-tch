using System.Security.Cryptography;

namespace Paybitch.Api.Features.Auth.Apple;

/// <summary>
/// AES-256-GCM envelope for the Apple refresh token at rest (§4.1 "encrypted at rest"). The stored
/// blob is <c>nonce(12) ‖ tag(16) ‖ ciphertext</c>. The key comes from
/// <see cref="AppleAuthOptions.TokenEncryptionKeyBase64"/> (32 bytes, base64); when absent, encryption
/// is unavailable and the caller simply skips persisting the token (never stores it in plaintext).
/// </summary>
public static class AppleTokenCipher
{
    private const int NonceSize = 12;   // AES-GCM standard nonce
    private const int TagSize = 16;     // AES-GCM 128-bit tag
    private const int KeySize = 32;     // AES-256

    /// <summary>Resolve the 32-byte key, or null when unconfigured / malformed.</summary>
    public static byte[]? TryResolveKey(AppleAuthOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.TokenEncryptionKeyBase64))
            return null;
        try
        {
            var key = Convert.FromBase64String(options.TokenEncryptionKeyBase64);
            return key.Length == KeySize ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Encrypt plaintext with the given key → <c>nonce ‖ tag ‖ ciphertext</c>.</summary>
    public static byte[] Encrypt(string plaintext, byte[] key)
    {
        var plain = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        var blob = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceSize);
        cipher.CopyTo(blob, NonceSize + TagSize);
        return blob;
    }

    /// <summary>Decrypt a <c>nonce ‖ tag ‖ ciphertext</c> blob; null on any tamper/format failure.</summary>
    public static string? Decrypt(byte[] blob, byte[] key)
    {
        if (blob.Length < NonceSize + TagSize)
            return null;
        try
        {
            var nonce = blob.AsSpan(0, NonceSize);
            var tag = blob.AsSpan(NonceSize, TagSize);
            var cipher = blob.AsSpan(NonceSize + TagSize);
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

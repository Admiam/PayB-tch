using System.Security.Cryptography;
using System.Text;

namespace Paybitch.Api.Features.Groups.Support;

/// <summary>
/// Issues <c>linkKey</c> — a per-caller, opaque, stable handle for the account behind a participant
/// row, so a client can tell that the Petr in one group and the Petr in another are one person.
/// </summary>
/// <remarks>
/// <para>
/// A member only exists inside a group, so nothing in the wire model ties one human's rows together.
/// The obvious fix — publish <c>group_members.user_id</c> — is the one thing <see cref="Members.MemberResponse"/>
/// deliberately refuses: a raw account id is a global identifier, and once two clients can pool them,
/// people can be correlated across groups that never agreed to know about each other.
/// </para>
/// <para>
/// So the key is HMAC-SHA256 over <c>callerUserId|memberUserId</c>, truncated to 128 bits. It answers
/// the only question a client needs — "are these two rows the same person?" — and nothing else:
/// <list type="bullet">
///   <item>stable for one caller across every group they can already see, which is what makes
///     auto-pairing possible;</item>
///   <item>different for every other caller, so two users comparing notes learn nothing;</item>
///   <item>not reversible to an account id without the server secret.</item>
/// </list>
/// Ghosts have no account and get <c>null</c> — there is genuinely nothing to match on, which is
/// exactly why they are the rows a human has to pair by hand.
/// </para>
/// <para>
/// Secret resolution mirrors <c>NotificationSecrets</c>: env → config → a marked DEV fallback with a
/// warning. A deployment that skips the secret still works and is still unlinkable across callers; what
/// degrades is only that the key becomes predictable to anyone holding the published dev constant.
/// </para>
/// </remarks>
public sealed class MemberLinkKeys
{
    public const string ConfigKey = "Identity:LinkKeySigningKey";
    private const string EnvKey = "PAYBITCH_IDENTITY_LINK_KEY";
    private const string DevKey = "dev-identity-link-key-not-for-production";

    /// <summary>128 bits — far past collision risk for one account's handful of groups, half the bytes on the wire.</summary>
    private const int KeyBytes = 16;

    private readonly byte[] _secret;

    public MemberLinkKeys(IConfiguration config, ILogger<MemberLinkKeys> logger)
    {
        var configured = Environment.GetEnvironmentVariable(EnvKey);
        if (string.IsNullOrWhiteSpace(configured))
            configured = config[ConfigKey];

        if (string.IsNullOrWhiteSpace(configured))
        {
            logger.LogWarning(
                "No {ConfigKey} configured — using an INSECURE DEV identity link key. Configure a real "
                + "secret for any shared deployment.", ConfigKey);
            configured = DevKey;
        }

        _secret = Encoding.UTF8.GetBytes(configured);
    }

    /// <summary>
    /// The caller's handle for <paramref name="memberUserId"/>, or <c>null</c> when either side is
    /// absent — an unauthenticated context has no caller to scope the key to, and a ghost has no
    /// account to name.
    /// </summary>
    public string? For(Guid? callerUserId, Guid? memberUserId)
    {
        if (callerUserId is not Guid caller || memberUserId is not Guid member)
            return null;

        var payload = Encoding.UTF8.GetBytes($"{caller:D}|{member:D}");
        var mac = HMACSHA256.HashData(_secret, payload);
        return Base64UrlEncode(mac.AsSpan(0, KeyBytes));
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

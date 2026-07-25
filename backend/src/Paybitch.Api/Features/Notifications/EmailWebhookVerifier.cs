using System.Security.Cryptography;

namespace Paybitch.Api.Features.Notifications;

/// <summary>
/// Signature gate for <c>POST /webhooks/email/{provider}</c> (EXT-D5d, §5.7). The endpoint mutates
/// <c>email_suppressions</c>, so it MUST verify the provider's signature BEFORE any write, else anyone
/// could suppress an arbitrary address (a targeted mail-DoS). This dev/default verifier is HMAC-based:
/// <c>HMAC-SHA256(secret, rawBody)</c> compared constant-time to the <c>X-Paybitch-Webhook-Signature</c>
/// header (hex). Fail-closed: a missing/mismatched/short signature is rejected.
/// </summary>
/// <remarks>
/// PROD (SES-EU): replace this with real Amazon SNS verification — validate the <c>Signature</c> against
/// the message's X.509 <c>SigningCertURL</c> cert and confirm the subscription. That path is a network +
/// X.509 step deliberately out of scope for the dev transport; the HMAC gate here is the swappable seam.
/// </remarks>
public static class EmailWebhookVerifier
{
    public const string SignatureHeader = "X-Paybitch-Webhook-Signature";

    public static bool Verify(NotificationSecrets secrets, ReadOnlySpan<byte> rawBody, string? signatureHex)
    {
        if (string.IsNullOrWhiteSpace(signatureHex))
            return false;

        byte[] presented;
        try
        {
            presented = Convert.FromHexString(signatureHex.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = secrets.SignWebhook(rawBody);
        return presented.Length == expected.Length
               && CryptographicOperations.FixedTimeEquals(presented, expected);
    }
}

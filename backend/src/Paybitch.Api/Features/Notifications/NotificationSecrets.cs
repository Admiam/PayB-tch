using System.Security.Cryptography;
using System.Text;

namespace Paybitch.Api.Features.Notifications;

/// <summary>
/// Resolves the server HMAC secrets for the two signed E5 surfaces — the unsubscribe token (EXT-D5h)
/// and the email webhook (EXT-D5d/§5.7). Resolution order is env → config → a marked DEV fallback with a
/// one-time warning, exactly mirroring <c>Es256KeyProvider</c>'s "runs with no secret in dev" posture.
/// A shared deployment MUST configure a real secret, else the fallback (a known constant) makes signing
/// forgeable. Fail-closed against UNSIGNED / mismatched input still holds even on the dev key: only the
/// secret's secrecy degrades in dev, not the verification.
/// </summary>
public sealed class NotificationSecrets
{
    // Config keys (Appendix A additions — see the convergence notes).
    public const string UnsubscribeConfigKey = "Notifications:UnsubscribeSigningKey";
    public const string WebhookConfigKey = "Notifications:WebhookSecret";
    private const string UnsubscribeEnvKey = "PAYBITCH_UNSUBSCRIBE_KEY";
    private const string WebhookEnvKey = "PAYBITCH_EMAIL_WEBHOOK_SECRET";

    // Deliberately-obvious dev fallbacks (never a real secret).
    private const string DevUnsubscribeKey = "dev-unsubscribe-key-not-for-production";
    private const string DevWebhookKey = "dev-email-webhook-secret-not-for-production";

    private readonly byte[] _unsubscribeKey;
    private readonly byte[] _webhookKey;

    public NotificationSecrets(IConfiguration config, ILogger<NotificationSecrets> logger)
    {
        _unsubscribeKey = Resolve(config, UnsubscribeConfigKey, UnsubscribeEnvKey, DevUnsubscribeKey, "unsubscribe token", logger);
        _webhookKey = Resolve(config, WebhookConfigKey, WebhookEnvKey, DevWebhookKey, "email webhook", logger);
    }

    /// <summary>HMAC-SHA256 the unsubscribe payload with the unsubscribe secret.</summary>
    public byte[] SignUnsubscribe(ReadOnlySpan<byte> payload) => HMACSHA256.HashData(_unsubscribeKey, payload);

    /// <summary>HMAC-SHA256 the raw webhook body with the webhook secret.</summary>
    public byte[] SignWebhook(ReadOnlySpan<byte> body) => HMACSHA256.HashData(_webhookKey, body);

    private static byte[] Resolve(
        IConfiguration config, string configKey, string envKey, string devFallback, string label, ILogger logger)
    {
        var configured = Environment.GetEnvironmentVariable(envKey);
        if (string.IsNullOrWhiteSpace(configured))
            configured = config[configKey];

        if (!string.IsNullOrWhiteSpace(configured))
            return Encoding.UTF8.GetBytes(configured);

        logger.LogWarning(
            "No {ConfigKey} configured — using an INSECURE DEV {Label} signing key. Configure a real secret for any shared deployment.",
            configKey, label);
        return Encoding.UTF8.GetBytes(devFallback);
    }
}

using System.ComponentModel.DataAnnotations;

namespace Paybitch.Api.Features.Platform.Email;

/// <summary>
/// SMTP relay settings for <see cref="SmtpEmailSender"/>. Deliberately transport-generic: any
/// provider with an SMTP submission endpoint works (SES, Postmark, Brevo, Fastmail, a self-hosted
/// relay), so switching providers is a config change rather than a code change.
/// <para>
/// Absent or with an empty <see cref="Host"/>, the app falls back to <see cref="LogEmailSender"/>
/// so a stock checkout still boots with zero configuration.
/// </para>
/// </summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    /// <summary>Relay hostname. Empty disables SMTP and keeps the log transport.</summary>
    public string Host { get; init; } = "";

    /// <summary>
    /// 587 (submission + STARTTLS) by default rather than 25, which many VPS providers block for
    /// outbound traffic.
    /// </summary>
    [Range(1, 65535)]
    public int Port { get; init; } = 587;

    public string Username { get; init; } = "";

    /// <summary>Supplied via environment or a secret store — never committed to appsettings.</summary>
    public string Password { get; init; } = "";

    /// <summary>
    /// STARTTLS on the submission port. Set false only for implicit TLS on 465, or for a local relay
    /// on a trusted network.
    /// </summary>
    public bool UseStartTls { get; init; } = true;

    [EmailAddress]
    public string FromAddress { get; init; } = "noreply@paybitch.app";

    public string FromName { get; init; } = "Paybitch";

    /// <summary>
    /// A send that hangs would otherwise hold an OTP request open, since that path sends inline.
    /// </summary>
    [Range(1, 120)]
    public int TimeoutSeconds { get; init; } = 15;

    /// <summary>True when enough is configured to actually deliver mail.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}

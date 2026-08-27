using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;
using Paybitch.Infrastructure;

namespace Paybitch.Api.Features.Platform.Email;

/// <summary>
/// The production <see cref="IEmailSender"/>: delivers over SMTP via MailKit.
/// <para>
/// SMTP rather than a provider SDK on purpose — it speaks to any relay, so the provider is a config
/// value instead of a dependency. Suppression is enforced here, not just by callers: the interface
/// documents that a send silently no-ops for a hard-suppressed recipient, and the OTP path relies on
/// that entirely because it never checks suppression itself.
/// </para>
/// </summary>
public sealed class SmtpEmailSender(
    AppDbContext db,
    IOptions<SmtpOptions> options,
    ILogger<SmtpEmailSender> logger) : IEmailSender
{
    // Mirrors LogEmailSender: the table's CHECK constraint only permits these today, but stating them
    // keeps the semantics if that table is ever widened with soft reasons.
    private static readonly string[] HardReasons = ["bounce", "complaint", "manual"];

    private readonly SmtpOptions _options = options.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (await IsSuppressedAsync(message.To, ct))
        {
            logger.LogInformation(
                "Email suppressed, not sending. To={To} Subject={Subject}", message.To, message.Subject);
            return;
        }

        var mime = BuildMessage(message);

        using var client = new SmtpClient
        {
            Timeout = _options.TimeoutSeconds * 1000,
        };

        try
        {
            var socketOptions = _options.UseStartTls
                ? SecureSocketOptions.StartTls
                : SecureSocketOptions.Auto;

            await client.ConnectAsync(_options.Host, _options.Port, socketOptions, ct);

            // A relay on a trusted network may accept unauthenticated submission.
            if (!string.IsNullOrEmpty(_options.Username))
                await client.AuthenticateAsync(_options.Username, _options.Password, ct);

            await client.SendAsync(mime, ct);
            await client.DisconnectAsync(quit: true, ct);

            logger.LogInformation(
                "Email sent. To={To} Subject={Subject}", message.To, message.Subject);
        }
        catch (Exception ex)
        {
            // Logged with the recipient and subject but never the body — a login code must not reach
            // the log stream. Rethrown so the queued path can retry and the inline OTP path surfaces
            // the failure rather than telling the user a code is on its way that never left.
            logger.LogError(
                ex, "Email send failed. To={To} Subject={Subject} Host={Host}",
                message.To, message.Subject, _options.Host);
            throw;
        }
    }

    public Task<bool> IsSuppressedAsync(string email, CancellationToken ct = default) =>
        // email is citext ⇒ the equality is case-insensitive at the DB level.
        db.EmailSuppressions.AnyAsync(s => s.Email == email && HardReasons.Contains(s.Reason), ct);

    private MimeMessage BuildMessage(EmailMessage message)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;

        // multipart/alternative: clients that refuse HTML still get a readable code.
        mime.Body = new BodyBuilder
        {
            HtmlBody = message.HtmlBody,
            TextBody = message.TextBody,
        }.ToMessageBody();

        return mime;
    }
}

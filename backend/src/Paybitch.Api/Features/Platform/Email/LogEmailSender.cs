using Microsoft.EntityFrameworkCore;
using Paybitch.Infrastructure;

namespace Paybitch.Api.Features.Platform.Email;

/// <summary>
/// The dev/default <see cref="IEmailSender"/>: checks suppression, then logs the message instead of
/// delivering it. To + subject are logged at Information; the rendered bodies only at Debug, so a normal
/// log stream never carries full message content. The production SES-EU transport sits behind the same
/// interface.
/// </summary>
public sealed class LogEmailSender(AppDbContext db, ILogger<LogEmailSender> logger) : IEmailSender
{
    // The email_suppressions table only ever holds hard reasons (CHECK constraint), but we state them
    // explicitly so the semantics survive any future widening of that table.
    private static readonly string[] HardReasons = ["bounce", "complaint", "manual"];

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (await IsSuppressedAsync(message.To, ct))
        {
            logger.LogInformation(
                "Email suppressed, not sending. To={To} Subject={Subject}", message.To, message.Subject);
            return;
        }

        logger.LogInformation(
            "Email send (dev log transport). To={To} Subject={Subject}", message.To, message.Subject);
        logger.LogDebug(
            "Email body. To={To} Subject={Subject} HtmlBody={HtmlBody} TextBody={TextBody}",
            message.To, message.Subject, message.HtmlBody, message.TextBody);
    }

    public Task<bool> IsSuppressedAsync(string email, CancellationToken ct = default) =>
        // email is citext ⇒ the equality is case-insensitive at the DB level.
        db.EmailSuppressions.AnyAsync(s => s.Email == email && HardReasons.Contains(s.Reason), ct);
}

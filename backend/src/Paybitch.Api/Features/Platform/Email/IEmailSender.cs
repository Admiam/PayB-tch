namespace Paybitch.Api.Features.Platform.Email;

/// <summary>
/// The E0 transactional email transport (§0.4.4). Rendering (localized cs/en templates) is owned by E5;
/// this interface takes an already-rendered <see cref="EmailMessage"/> and delivers it. Every send first
/// consults <see cref="IsSuppressedAsync"/> so a hard-suppressed address is never re-mailed. The
/// production target is SES-EU behind this same interface; <see cref="LogEmailSender"/> is the dev impl.
/// </summary>
public interface IEmailSender
{
    /// <summary>Deliver <paramref name="message"/>. Silently no-ops when the recipient is hard-suppressed.</summary>
    Task SendAsync(EmailMessage message, CancellationToken ct = default);

    /// <summary>
    /// True when <paramref name="email"/> carries a <b>hard</b> deliverability suppression
    /// (<c>reason IN ('bounce','complaint','manual')</c>). Digest opt-out is deliberately NOT a
    /// suppression (it lives in <c>users.digest_opt_in</c>), so this never drops transactional mail
    /// such as a magic link or invite (EXT-D5i — no account-recovery DoS).
    /// </summary>
    Task<bool> IsSuppressedAsync(string email, CancellationToken ct = default);
}

/// <summary>A server-side-rendered transactional message. Both bodies are final MIME parts (E5 renders them).</summary>
public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody);

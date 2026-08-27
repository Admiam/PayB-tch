using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Paybitch.Api.Features.Platform.Email;
using Paybitch.Api.Features.Platform.Jobs;
using Paybitch.Infrastructure;

namespace Paybitch.Api.Features.Notifications.Email;

/// <summary>
/// The <c>email.send</c> job handler (E5, §5.4). Idempotent + at-least-once (X5): it re-checks HARD
/// suppression (and, for a digest only, <c>users.digest_opt_in</c> — EXT-D5i) at send time, hydrates
/// names from ids, renders the cs/en template, and delivers via <c>IEmailSender</c>. A suppressed /
/// consent-revoked recipient is a silent no-op (still a success — nothing to retry). The enqueuer's
/// dedupe key (<c>notify:{activityId}:{userId}</c> for fan-out) is what guarantees exactly-one email.
/// </summary>
public sealed class EmailSendHandler(
    AppDbContext db,
    IEmailSender emailSender,
    IConfiguration config,
    ILoggerFactory loggerFactory) : IJobHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string Kind => NotificationJobKinds.EmailSend;

    public async Task HandleAsync(JobContext job, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger<EmailSendHandler>();

        var payload = JsonSerializer.Deserialize<EmailSendPayload>(job.Payload, JsonOptions);
        if (payload is null || string.IsNullOrWhiteSpace(payload.To) || string.IsNullOrWhiteSpace(payload.Template))
        {
            logger.LogWarning("email.send job {JobId} has an empty To/Template; dropping", job.Id);
            return; // malformed → drop (no retry storm); never log the payload/token
        }

        if (!EmailTemplateKinds.IsKnown(payload.Template))
        {
            logger.LogWarning("email.send job {JobId} has unknown template {Template}; dropping", job.Id, payload.Template);
            return;
        }

        // Transactional AND digest both require: address not HARD-suppressed (EXT-D5i).
        if (await emailSender.IsSuppressedAsync(payload.To, ct))
        {
            logger.LogInformation("email.send skipped: {Template} recipient is hard-suppressed", payload.Template);
            return;
        }

        // Digest ADDITIONALLY requires consent; transactional never does (no account-recovery DoS, EXT-D5i).
        if (payload.Template == EmailTemplateKinds.Digest)
        {
            var optIn = payload.UserId is { } uid
                ? await db.Users.Where(u => u.Id == uid && u.DeletedAt == null)
                    .Select(u => (bool?)u.DigestOptIn).FirstOrDefaultAsync(ct)
                : null;
            if (optIn != true)
            {
                logger.LogInformation("email.send skipped: digest consent absent");
                return;
            }
        }

        var model = await BuildModelAsync(payload, ct);
        var message = EmailTemplates.Render(payload.To, model);
        await emailSender.SendAsync(message, ct);
    }

    private async Task<EmailModel> BuildModelAsync(EmailSendPayload p, CancellationToken ct)
    {
        var locale = p.Locale;
        if (string.IsNullOrWhiteSpace(locale) && p.UserId is { } userId)
            locale = await db.Users.Where(u => u.Id == userId).Select(u => u.Locale).FirstOrDefaultAsync(ct);
        locale ??= "en";

        var groupName = p.GroupId is { } gid
            ? await db.Groups.Where(g => g.Id == gid).Select(g => g.Name).FirstOrDefaultAsync(ct)
            : null;

        var actorName = p.ActorUserId is { } aid
            ? await db.Users.Where(u => u.Id == aid).Select(u => u.DisplayName).FirstOrDefaultAsync(ct)
            : null;

        var baseUrl = (config["App:PublicBaseUrl"] ?? "https://paybitch.app").TrimEnd('/');
        // The landing is served at /v1/i/ today (the IEndpointModule /v1 group); flip this to "/i/" once
        // the orchestrator root-mounts it (see convergence notes).
        var landingPath = config["App:InviteLandingPath"] ?? "/v1/i/";

        return p.Template switch
        {
            EmailTemplateKinds.Invite => new EmailModel(
                p.Template, locale, GroupName: groupName, InviterName: actorName,
                Link: p.Token is null ? null : $"{baseUrl}{landingPath}{p.Token}"),

            EmailTemplateKinds.MagicLink => new EmailModel(
                p.Template, locale, Code: p.Token,
                Link: p.Token is null ? null : $"{baseUrl}/auth/email/verify?code={Uri.EscapeDataString(p.Token)}"),

            EmailTemplateKinds.ExportReady => new EmailModel(
                p.Template, locale,
                Link: p.EntityId is null ? null : $"{baseUrl}/v1/exports/{p.EntityId}"),

            EmailTemplateKinds.Digest => new EmailModel(
                p.Template, locale,
                UnsubscribeUrl: p.UserId is null ? null : BuildUnsubscribeUrl(baseUrl, p.UserId.Value)),

            _ => new EmailModel(p.Template, locale, GroupName: groupName, ActorName: actorName),
        };
    }

    private string BuildUnsubscribeUrl(string baseUrl, Guid userId)
    {
        var secrets = new NotificationSecrets(config, loggerFactory.CreateLogger<NotificationSecrets>());
        var token = UnsubscribeToken.Issue(secrets, userId);
        return $"{baseUrl}/v1/unsubscribe?token={Uri.EscapeDataString(token)}";
    }
}

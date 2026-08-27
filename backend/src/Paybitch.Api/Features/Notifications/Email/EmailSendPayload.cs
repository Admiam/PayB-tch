using System.Text.Json.Serialization;

namespace Paybitch.Api.Features.Notifications.Email;

/// <summary>
/// The <c>email.send</c> job payload (ids + the transactional credential where one is intrinsic to the
/// mail, §4.3). The fan-out worker and other slices (invite-create, magic-link, export-ready) enqueue
/// this; <see cref="EmailSendHandler"/> re-checks suppression/consent, hydrates names from ids, renders
/// the cs/en template, and delivers via <c>IEmailSender</c>.
/// </summary>
/// <remarks>
/// <c>Token</c> carries the raw invite token / magic-link code — the ONLY exposure boundary where it must
/// travel (the mail link needs it). Jobs are short-lived and the handler never logs the payload, so the
/// token stays out of logs (EXT-D5f).
/// </remarks>
public sealed record EmailSendPayload(
    [property: JsonPropertyName("template")] string Template,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("locale")] string? Locale = null,
    [property: JsonPropertyName("userId")] Guid? UserId = null,
    [property: JsonPropertyName("groupId")] Guid? GroupId = null,
    [property: JsonPropertyName("actorUserId")] Guid? ActorUserId = null,
    [property: JsonPropertyName("entityId")] Guid? EntityId = null,
    [property: JsonPropertyName("verb")] string? Verb = null,
    [property: JsonPropertyName("token")] string? Token = null);

/// <summary>The rendered cs/en template selector (EXT-D5d). <c>Digest</c> additionally checks consent.</summary>
public static class EmailTemplateKinds
{
    public const string Invite = "invite";
    public const string MagicLink = "magic_link";
    public const string ExportReady = "export_ready";
    public const string Digest = "digest";
    public const string Activity = "activity";

    /// <summary>Templates that are transactional — sent iff not HARD-suppressed (never gated on consent, EXT-D5i).</summary>
    public static readonly IReadOnlySet<string> Transactional = new HashSet<string>(StringComparer.Ordinal)
    {
        Invite, MagicLink, ExportReady, Activity,
    };

    public static bool IsKnown(string template) =>
        template is Invite or MagicLink or ExportReady or Digest or Activity;
}

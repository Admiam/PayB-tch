using System.Net;
using Paybitch.Api.Features.Platform.Email;

namespace Paybitch.Api.Features.Notifications.Email;

/// <summary>
/// The already-hydrated data an email template renders from. All names are resolved from ids by the
/// handler BEFORE rendering (the template does no DB work). User-controlled strings (group/actor names)
/// are HTML-escaped at render time (stored-XSS defense, §5.7).
/// </summary>
public sealed record EmailModel(
    string Template,
    string Locale,
    string? GroupName = null,
    string? ActorName = null,
    string? InviterName = null,
    string? Link = null,
    string? Code = null,
    string? UnsubscribeUrl = null);

/// <summary>
/// In-app cs/en transactional templates (EXT-D5d) rendered to an <see cref="EmailMessage"/>. Rendering
/// in-app keeps the provider a config swap (SES-EU today), not a template migration. Both a text and an
/// HTML part are produced; HTML interpolations of user-controlled values are escaped.
/// </summary>
public static class EmailTemplates
{
    public static EmailMessage Render(string to, EmailModel model)
    {
        var cs = IsCzech(model.Locale);
        return model.Template switch
        {
            EmailTemplateKinds.Invite => Invite(to, model, cs),
            EmailTemplateKinds.MagicLink => MagicLink(to, model, cs),
            EmailTemplateKinds.ExportReady => ExportReady(to, model, cs),
            EmailTemplateKinds.Digest => Digest(to, model, cs),
            _ => Activity(to, model, cs),
        };
    }

    private static bool IsCzech(string? locale) =>
        locale is not null && locale.StartsWith("cs", StringComparison.OrdinalIgnoreCase);

    private static EmailMessage Invite(string to, EmailModel m, bool cs)
    {
        var group = m.GroupName ?? (cs ? "skupina" : "a group");
        var inviter = m.InviterName ?? (cs ? "Někdo" : "Someone");
        var link = m.Link ?? "";
        var subject = cs ? $"{inviter} vás zve do „{group}“ na Paybitch"
                         : $"{inviter} invited you to \"{group}\" on Paybitch";
        var text = cs
            ? $"{inviter} vás zve do skupiny „{group}“ na Paybitch.\n\nOtevřete pozvánku: {link}\n"
            : $"{inviter} invited you to the group \"{group}\" on Paybitch.\n\nOpen the invite: {link}\n";
        var html = Wrap(subject,
            $"<p>{E(cs ? $"{inviter} vás zve do skupiny „{group}“ na Paybitch." : $"{inviter} invited you to the group \"{group}\" on Paybitch.")}</p>"
            + Button(link, cs ? "Otevřít pozvánku" : "Open invite"));
        return new EmailMessage(to, subject, html, text);
    }

    private static EmailMessage MagicLink(string to, EmailModel m, bool cs)
    {
        var link = m.Link ?? "";
        var code = m.Code;
        var subject = cs ? "Váš přihlašovací odkaz do Paybitch" : "Your Paybitch sign-in link";
        var codeLine = code is null ? "" : (cs ? $"\nKód: {code}\n" : $"\nCode: {code}\n");
        var text = cs
            ? $"Přihlaste se do Paybitch tímto odkazem: {link}\n{codeLine}\nPokud jste to nebyli vy, ignorujte tento e-mail.\n"
            : $"Sign in to Paybitch with this link: {link}\n{codeLine}\nIf this wasn't you, ignore this email.\n";
        var htmlCode = code is null ? "" : $"<p>{E(cs ? $"Kód: {code}" : $"Code: {code}")}</p>";
        var html = Wrap(subject,
            $"<p>{E(cs ? "Přihlaste se do Paybitch." : "Sign in to Paybitch.")}</p>"
            + Button(link, cs ? "Přihlásit se" : "Sign in") + htmlCode);
        return new EmailMessage(to, subject, html, text);
    }

    private static EmailMessage ExportReady(string to, EmailModel m, bool cs)
    {
        var link = m.Link ?? "";
        var subject = cs ? "Váš export z Paybitch je připraven" : "Your Paybitch export is ready";
        var text = cs
            ? $"Váš export je připraven ke stažení: {link}\n"
            : $"Your export is ready to download: {link}\n";
        var html = Wrap(subject,
            $"<p>{E(cs ? "Váš export je připraven ke stažení." : "Your export is ready to download.")}</p>"
            + Button(link, cs ? "Stáhnout" : "Download"));
        return new EmailMessage(to, subject, html, text);
    }

    private static EmailMessage Activity(string to, EmailModel m, bool cs)
    {
        var group = m.GroupName ?? (cs ? "skupině" : "a group");
        var actor = m.ActorName ?? (cs ? "Někdo" : "Someone");
        var subject = cs ? $"Nová aktivita ve skupině „{group}“" : $"New activity in \"{group}\"";
        var text = cs
            ? $"{actor} má novou aktivitu ve skupině „{group}“ na Paybitch.\n"
            : $"{actor} has new activity in the group \"{group}\" on Paybitch.\n";
        var html = Wrap(subject,
            $"<p>{E(cs ? $"{actor} má novou aktivitu ve skupině „{group}“." : $"{actor} has new activity in \"{group}\".")}</p>");
        return new EmailMessage(to, subject, html, text);
    }

    private static EmailMessage Digest(string to, EmailModel m, bool cs)
    {
        var subject = cs ? "Váš týdenní přehled z Paybitch" : "Your weekly Paybitch summary";
        var unsub = m.UnsubscribeUrl;
        var textFooter = unsub is null ? "" : (cs ? $"\nOdhlásit odběr: {unsub}\n" : $"\nUnsubscribe: {unsub}\n");
        var text = (cs
            ? "Týdenní přehled vašich skupin na Paybitch.\n"
            : "A weekly summary of your Paybitch groups.\n") + textFooter;
        var htmlFooter = unsub is null ? "" :
            $"<p style=\"font-size:12px;color:#888\"><a href=\"{E(unsub)}\">{E(cs ? "Odhlásit odběr" : "Unsubscribe")}</a></p>";
        var html = Wrap(subject,
            $"<p>{E(cs ? "Týdenní přehled vašich skupin na Paybitch." : "A weekly summary of your Paybitch groups.")}</p>" + htmlFooter);
        return new EmailMessage(to, subject, html, text);
    }

    private static string Wrap(string title, string body) =>
        $"""
         <!doctype html><html><head><meta charset="utf-8" /><title>{E(title)}</title></head>
         <body style="font-family:system-ui,sans-serif;max-width:32rem;margin:2rem auto;padding:0 1rem">
         {body}
         </body></html>
         """;

    private static string Button(string href, string label) =>
        $"<p><a href=\"{E(href)}\" style=\"display:inline-block;padding:.6rem 1rem;background:#111;color:#fff;text-decoration:none;border-radius:6px\">{E(label)}</a></p>";

    private static string E(string value) => WebUtility.HtmlEncode(value);
}

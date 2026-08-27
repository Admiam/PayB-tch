using System.Net;
using System.Text;
using Paybitch.Api.Features.Platform.Email;

namespace Paybitch.Api.Features.EmailAuth;

/// <summary>
/// Renders the E6 transactional messages the OTP flow sends (§6.4/§6.6). Kept intentionally minimal —
/// full localized cs/en template ownership is E5's; this builds a plain-text-first code email plus the
/// login universal link, and the email-change grace notice. Bodies carry NO ledger data (§6.6).
/// </summary>
internal static class OtpEmailFactory
{
    /// <summary>The code email for a given purpose. Only <c>login</c> embeds the tap-to-login universal link (§6.4).</summary>
    public static EmailMessage BuildCode(string purpose, string email, string code, string locale, string linkBase)
    {
        var czech = IsCzech(locale);
        var (subject, lead) = purpose switch
        {
            OtpPurposes.Link => czech
                ? ("Paybitch – ověřovací kód pro propojení e-mailu", "Kód pro propojení e-mailu s vaším účtem:")
                : ("Paybitch – code to link your email", "Code to link this email to your account:"),
            OtpPurposes.VerifyChange => czech
                ? ("Paybitch – ověřovací kód pro změnu e-mailu", "Kód pro potvrzení nové e-mailové adresy:")
                : ("Paybitch – code to change your email", "Code to confirm your new email address:"),
            _ => czech
                ? ("Paybitch – přihlašovací kód", "Váš přihlašovací kód:")
                : ("Paybitch – your sign-in code", "Your sign-in code:"),
        };

        var expiry = czech ? "Platí 10 minut." : "Valid for 10 minutes.";
        var text = new StringBuilder()
            .AppendLine(lead)
            .AppendLine()
            .AppendLine(code)
            .AppendLine()
            .AppendLine(expiry);

        // The universal link only makes sense for a login on a fresh device (EXT-D6g). It pre-fills the code.
        if (purpose == OtpPurposes.Login)
        {
            var link = $"{linkBase}?e={Uri.EscapeDataString(email)}&c={Uri.EscapeDataString(code)}";
            text.AppendLine().AppendLine(czech ? "Nebo klepněte pro přihlášení:" : "Or tap to sign in:").AppendLine(link);
        }

        var textBody = text.ToString();
        var htmlBody =
            $"<p>{WebUtility.HtmlEncode(lead)}</p>" +
            $"<p style=\"font-size:24px;font-weight:bold;letter-spacing:3px\">{WebUtility.HtmlEncode(code)}</p>" +
            $"<p>{WebUtility.HtmlEncode(expiry)}</p>";
        return new EmailMessage(email, subject, htmlBody, textBody);
    }

    /// <summary>The informational security notice to the OLD address on an email change (EXT-D6h). Not a gate.</summary>
    public static EmailMessage BuildChangeNotice(string oldEmail, string locale)
    {
        var czech = IsCzech(locale);
        var subject = czech ? "Paybitch – e-mail účtu byl změněn" : "Paybitch – your account email was changed";
        var body = czech
            ? "E-mailová adresa vašeho účtu Paybitch byla změněna. Pokud jste to nebyli vy, kontaktujte podporu."
            : "The email address on your Paybitch account was changed. If this wasn't you, contact support.";
        return new EmailMessage(oldEmail, subject, $"<p>{WebUtility.HtmlEncode(body)}</p>", body);
    }

    private static bool IsCzech(string? locale)
        => locale is not null && locale.StartsWith("cs", StringComparison.OrdinalIgnoreCase);
}

namespace Paybitch.Api.Features.Comments;

/// <summary>
/// E10a problem <c>code</c>s (§7.7, additive to Appendix B). These are NOT yet in the shared
/// <see cref="Paybitch.Api.Common.Errors.ProblemCodes"/> catalog, so handlers pass them as string
/// literals through <c>Problems.Validation(code,…)</c> / <c>Problems.Forbidden(code)</c> — that sets the
/// wire <c>code</c> directly, independent of the validation-filter twin table (mirrors
/// <c>RecurringProblemCodes</c>). The orchestrator should add them to <c>ProblemCodes</c> for full
/// catalog consistency.
/// </summary>
public static class CommentProblemCodes
{
    /// <summary>422 — body was null/empty (the <c>length ≥ 1</c> twin, §7.3 CHECK).</summary>
    public const string CommentEmpty = "comment_empty";

    /// <summary>422 — body exceeded the 2000-char ceiling (the <c>length ≤ 2000</c> twin, §7.3 CHECK).</summary>
    public const string CommentTooLong = "comment_too_long";

    /// <summary>403 — a non-author tried to edit; an admin may tombstone but never rewrite (EXT-DC5).</summary>
    public const string CommentEditForbidden = "comment_edit_forbidden";

    /// <summary>403 — a non-author non-admin tried to delete (EXT-DC5).</summary>
    public const string CommentDeleteForbidden = "comment_delete_forbidden";
}

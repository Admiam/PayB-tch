using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Paybitch.Api.Common.Errors;

/// <summary>
/// Factory for RFC 9457 problem+json <see cref="IResult"/> responses, one helper per shape a handler
/// returns. Every helper resolves a stable default <c>title</c> from the catalog (<see cref="Titles"/>)
/// so callers pass only a <see cref="ProblemCodes"/> value. Never echo raw exception text or ids in
/// <c>detail</c> — the D6 no-existence-leak rule (§3.4).
/// </summary>
public static class Problems
{
    /// <summary>Canonical human titles per catalog code (Appendix B). Missing ⇒ the code itself.</summary>
    private static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>
    {
        [ProblemCodes.Unauthenticated] = "Authentication required",
        [ProblemCodes.TokenEpochStale] = "Access token epoch is stale; refresh",
        [ProblemCodes.AppleTokenInvalid] = "Apple identity token is invalid",
        [ProblemCodes.InsufficientRole] = "Insufficient role for this action",
        [ProblemCodes.SettlementVoidForbidden] = "Not permitted to void this settlement",
        [ProblemCodes.NotFound] = "Not found",
        [ProblemCodes.BalanceNotZero] = "Member balance is not settled",
        [ProblemCodes.GroupArchived] = "Group is archived",
        [ProblemCodes.AlreadyMember] = "Already a member of this group",
        [ProblemCodes.GhostAlreadyClaimed] = "Ghost member already claimed",
        [ProblemCodes.LastOwner] = "Group must keep at least one owner",
        [ProblemCodes.InviteAlreadyAccepted] = "Invite already accepted",
        [ProblemCodes.CategoryExists] = "A category with that name already exists",
        [ProblemCodes.ClientIdConflict] = "clientId already used in this group with different content",
        [ProblemCodes.LimitExceeded] = "An operational cap was exceeded",
        [ProblemCodes.InviteExpired] = "Invite has expired",
        [ProblemCodes.InviteConsumed] = "Invite has already been used",
        [ProblemCodes.SyncCursorExpired] = "Sync cursor has expired; re-bootstrap",
        [ProblemCodes.VersionConflict] = "Version conflict",
        [ProblemCodes.PayloadTooLarge] = "Request body is too large",
        [ProblemCodes.ValidationFailed] = "One or more fields are invalid",
        [ProblemCodes.TitleLength] = "Title must be 1–140 characters",
        [ProblemCodes.NameLength] = "Name must be 1–100 characters",
        [ProblemCodes.AmountNotPositive] = "Amount must be a positive integer of minor units",
        [ProblemCodes.DateOutOfRange] = "Date is out of the allowed range",
        [ProblemCodes.NotesTooLong] = "Notes exceed the maximum length",
        [ProblemCodes.UnsupportedCurrency] = "Currency is not supported in v1",
        [ProblemCodes.InvalidSplitType] = "Unknown split type",
        [ProblemCodes.SplitSumMismatch] = "Split shares must sum to the expense amount",
        [ProblemCodes.SplitPercentSumMismatch] = "Split percentages must sum to 10000 basis points",
        [ProblemCodes.ShareNegative] = "A split share amount is negative",
        [ProblemCodes.InvalidWeight] = "A split weight must be an integer ≥ 1",
        [ProblemCodes.InvalidBasisPoints] = "Basis points must be an integer ≥ 1",
        [ProblemCodes.SplitMemberInvalid] = "Split participant set is invalid",
        [ProblemCodes.SelfSettlement] = "A settlement cannot be from and to the same member",
        [ProblemCodes.ImmutableField] = "An immutable field cannot be changed",
        [ProblemCodes.NotAGhost] = "Target member is not an unclaimed ghost",
        [ProblemCodes.ClaimMemberMismatch] = "claimMemberId does not match the invite",
        [ProblemCodes.LedgerAcceptanceRequired] = "Inherited ledger must be explicitly accepted",
        [ProblemCodes.GhostCannotHoldRole] = "A ghost member cannot hold a role",
        [ProblemCodes.MemberDeleted] = "Member has been removed",
        [ProblemCodes.CategoryDeleted] = "Category has been deleted",
        [ProblemCodes.ForceUpdateRequired] = "A newer client build is required",
        [ProblemCodes.PreconditionRequired] = "If-Match is required for this mutation",
        [ProblemCodes.RateLimited] = "Rate limit exceeded",
        // E3 recurring
        [ProblemCodes.ReservedClientId] = "clientId prefix is reserved",
        [ProblemCodes.InvalidRecurrence] = "Recurrence rule is invalid",
        [ProblemCodes.InvalidTimezone] = "Timezone is not a valid IANA identifier",
        [ProblemCodes.RecurrenceEndAmbiguous] = "Recurrence end is ambiguous; specify only one of endsOn or count",
        [ProblemCodes.RecurringSplitRequired] = "Recurring rule requires a split template",
        [ProblemCodes.RuleNotResumable] = "Recurring rule has ended and cannot be resumed",
        // E4 exports
        [ProblemCodes.ExportExpired] = "Export has expired",
        // E5 notifications
        [ProblemCodes.InvalidChannel] = "Unknown notification channel",
        [ProblemCodes.UnknownEventType] = "Unknown notification event type",
        // E6 email auth
        [ProblemCodes.InvalidOrExpiredCode] = "Code is invalid or has expired",
        [ProblemCodes.RelayEmailNotAllowed] = "Private relay email is not allowed here",
        [ProblemCodes.IdentityTaken] = "That identity is already linked to another account",
        [ProblemCodes.LastIdentity] = "Cannot unlink the last identity",
        // E7 avatars
        [ProblemCodes.UnsupportedMediaType] = "Unsupported media type",
        [ProblemCodes.BlobTooLarge] = "Uploaded file is too large",
        // E10a comments
        [ProblemCodes.CommentEmpty] = "Comment body must not be empty",
        [ProblemCodes.CommentTooLong] = "Comment body exceeds the maximum length",
        [ProblemCodes.CommentEditForbidden] = "Not permitted to edit this comment",
        [ProblemCodes.CommentDeleteForbidden] = "Not permitted to delete this comment",
    };

    /// <summary>The stable title for a code, falling back to the code itself.</summary>
    public static string TitleFor(string code) => Titles.TryGetValue(code, out var t) ? t : code;

    /// <summary>Generic constructor. Prefer the shape-specific helpers below.</summary>
    public static IResult Create(
        int status,
        string code,
        string? title = null,
        string? detail = null,
        IReadOnlyDictionary<string, object?>? extensions = null,
        IReadOnlyDictionary<string, string>? headers = null)
        => new ProblemResult(status, code, title ?? TitleFor(code), detail, extensions, headers);

    // --- 401 ---
    public static IResult Unauthenticated(string code = ProblemCodes.Unauthenticated)
        => Create(StatusCodes.Status401Unauthorized, code);

    // --- 403 ---
    /// <summary>A proven member lacks the required role (D6: never shown to non-members — they get 404).</summary>
    public static IResult Forbidden(string code = ProblemCodes.InsufficientRole)
        => Create(StatusCodes.Status403Forbidden, code);

    // --- 404 (D6 uniform body — identical for "absent" and "not a member") ---
    public static IResult NotFound()
        => Create(
            StatusCodes.Status404NotFound,
            ProblemCodes.NotFound,
            detail: "The requested resource was not found.");

    // --- 409 ---
    public static IResult Conflict(string code, IReadOnlyDictionary<string, object?>? extensions = null)
        => Create(StatusCodes.Status409Conflict, code, extensions: extensions);

    /// <summary>Replayed create whose canonical fields diverge (§3.4 idempotency); body carries <c>existingId</c>.</summary>
    public static IResult ClientIdConflict(string existingId)
        => Conflict(ProblemCodes.ClientIdConflict, new Dictionary<string, object?> { ["existingId"] = existingId });

    // --- 410 ---
    public static IResult Gone(string code)
        => Create(StatusCodes.Status410Gone, code);

    // --- 412 (D8: body carries the current representation to merge against) ---
    public static IResult VersionConflict(object current)
        => Create(
            StatusCodes.Status412PreconditionFailed,
            ProblemCodes.VersionConflict,
            extensions: new Dictionary<string, object?> { ["current"] = current });

    // --- 413 ---
    public static IResult PayloadTooLarge()
        => Create(StatusCodes.Status413PayloadTooLarge, ProblemCodes.PayloadTooLarge);

    // --- 422 ---
    /// <summary>Generic field-shape failure with per-field <c>errors[]</c>.</summary>
    public static IResult ValidationFailed(IEnumerable<ProblemError> errors)
        => Validation(ProblemCodes.ValidationFailed, errors);

    /// <summary>A specific 422 twin (§3.4) — e.g. <c>title_length</c>, <c>split_sum_mismatch</c> — with <c>errors[]</c>.</summary>
    public static IResult Validation(string code, IEnumerable<ProblemError> errors, string? title = null)
        => Create(
            StatusCodes.Status422UnprocessableEntity,
            code,
            title,
            extensions: new Dictionary<string, object?> { ["errors"] = errors.ToArray() });

    /// <summary>A single-field 422 twin convenience.</summary>
    public static IResult Validation(string code, string field, string message, string? title = null)
        => Validation(code, [new ProblemError(field, message)], title);

    // --- 428 ---
    public static IResult PreconditionRequired()
        => Create(StatusCodes.Status428PreconditionRequired, ProblemCodes.PreconditionRequired);

    // --- 429 (Retry-After header set, §4.3) ---
    public static IResult TooManyRequests(TimeSpan? retryAfter = null)
    {
        var headers = retryAfter is { } ra
            ? new Dictionary<string, string>
            {
                ["Retry-After"] = ((int)Math.Ceiling(ra.TotalSeconds)).ToString(CultureInfo.InvariantCulture),
            }
            : null;
        return Create(StatusCodes.Status429TooManyRequests, ProblemCodes.RateLimited, headers: headers);
    }
}

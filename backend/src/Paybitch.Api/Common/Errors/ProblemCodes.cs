using System.Reflection;

namespace Paybitch.Api.Common.Errors;

/// <summary>
/// The v1 problem-code catalog (BACKEND_DESIGN Appendix B) — the complete closed list. Every
/// non-2xx problem+json body carries one of these as its stable machine <c>code</c>. Codes are
/// <c>snake_case</c>, stable forever, and part of the wire contract: adding is additive (§3.11),
/// renaming or removing is breaking.
/// </summary>
public static class ProblemCodes
{
    /// <summary>Base URI for the <c>type</c> member; the code is appended.</summary>
    public const string TypeBase = "https://api.paybitch.app/problems/";

    // 401
    public const string Unauthenticated = "unauthenticated";
    public const string TokenEpochStale = "token_epoch_stale";
    public const string AppleTokenInvalid = "apple_token_invalid";
    public const string InvalidOrExpiredCode = "invalid_or_expired_code"; // E6

    // 403
    public const string InsufficientRole = "insufficient_role";
    public const string SettlementVoidForbidden = "settlement_void_forbidden";
    public const string CommentEditForbidden = "comment_edit_forbidden";     // E10a
    public const string CommentDeleteForbidden = "comment_delete_forbidden"; // E10a

    // 404 (uniform D6 body — resource absent OR caller isn't a member)
    public const string NotFound = "not_found";

    // 409
    public const string BalanceNotZero = "balance_not_zero";
    public const string GroupArchived = "group_archived";
    public const string AlreadyMember = "already_member";
    public const string GhostAlreadyClaimed = "ghost_already_claimed";
    public const string LastOwner = "last_owner";
    public const string InviteAlreadyAccepted = "invite_already_accepted";
    public const string CategoryExists = "category_exists";
    public const string ClientIdConflict = "client_id_conflict";
    public const string LimitExceeded = "limit_exceeded";
    public const string RuleNotResumable = "rule_not_resumable"; // E3
    public const string IdentityTaken = "identity_taken";        // E6
    public const string LastIdentity = "last_identity";          // E6

    // 410
    public const string InviteExpired = "invite_expired";
    public const string InviteConsumed = "invite_consumed";
    public const string SyncCursorExpired = "sync_cursor_expired";
    public const string ExportExpired = "export_expired"; // E4

    // 412
    public const string VersionConflict = "version_conflict";

    // 413
    public const string PayloadTooLarge = "payload_too_large";
    public const string BlobTooLarge = "blob_too_large"; // E7

    // 415
    public const string UnsupportedMediaType = "unsupported_media_type"; // E7

    // 422
    public const string ValidationFailed = "validation_failed";
    public const string TitleLength = "title_length";
    public const string NameLength = "name_length";
    public const string AmountNotPositive = "amount_not_positive";
    public const string DateOutOfRange = "date_out_of_range";
    public const string NotesTooLong = "notes_too_long";
    public const string UnsupportedCurrency = "unsupported_currency";
    public const string InvalidSplitType = "invalid_split_type";
    public const string SplitSumMismatch = "split_sum_mismatch";
    public const string SplitPercentSumMismatch = "split_percent_sum_mismatch";
    public const string ShareNegative = "share_negative";
    public const string InvalidWeight = "invalid_weight";
    public const string InvalidBasisPoints = "invalid_basis_points";
    public const string SplitMemberInvalid = "split_member_invalid";
    public const string SelfSettlement = "self_settlement";
    public const string ImmutableField = "immutable_field";
    public const string NotAGhost = "not_a_ghost";
    public const string ClaimMemberMismatch = "claim_member_mismatch";
    public const string LedgerAcceptanceRequired = "ledger_acceptance_required";
    public const string GhostCannotHoldRole = "ghost_cannot_hold_role";
    public const string MemberDeleted = "member_deleted";
    public const string CategoryDeleted = "category_deleted";
    public const string ReservedClientId = "reserved_client_id";           // E3 — user clientId matching ^rec:
    public const string InvalidRecurrence = "invalid_recurrence";          // E3
    public const string InvalidTimezone = "invalid_timezone";              // E3
    public const string RecurrenceEndAmbiguous = "recurrence_end_ambiguous"; // E3
    public const string RecurringSplitRequired = "recurring_split_required"; // E3
    public const string InvalidChannel = "invalid_channel";                // E5
    public const string UnknownEventType = "unknown_event_type";           // E5
    public const string RelayEmailNotAllowed = "relay_email_not_allowed";  // E6
    public const string CommentEmpty = "comment_empty";                    // E10a
    public const string CommentTooLong = "comment_too_long";               // E10a

    // 426 / 428 / 429
    public const string ForceUpdateRequired = "force_update_required";
    public const string PreconditionRequired = "precondition_required";
    public const string RateLimited = "rate_limited";

    /// <summary>The fully-qualified <c>type</c> URI for a code.</summary>
    public static string TypeFor(string code) => TypeBase + code;

    /// <summary>
    /// The closed set of catalog codes (every <c>string</c> const except <see cref="TypeBase"/>),
    /// reflected once. Used to distinguish an intentional twin <c>ErrorCode</c> from FluentValidation's
    /// default validator-type names when resolving a 422's top-level code.
    /// </summary>
    public static readonly IReadOnlySet<string> All = typeof(ProblemCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string) && f.Name != nameof(TypeBase))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToHashSet();
}

namespace Paybitch.Api.Features.Activity;

/// <summary>
/// The closed §3.10 verb taxonomy — stable machine codes written to <c>activity_log.verb</c> by the
/// mutating handlers (in the same transaction as the mutation). Adding a verb is an additive change
/// (§3.11); renaming or removing one is breaking.
/// </summary>
public static class ActivityVerbs
{
    // expense
    public const string ExpenseCreated = "expense.created";
    public const string ExpenseUpdated = "expense.updated";
    public const string ExpenseDeleted = "expense.deleted";

    // settlement
    public const string SettlementRecorded = "settlement.recorded";
    public const string SettlementVoided = "settlement.voided";

    // member
    public const string MemberAdded = "member.added";
    public const string MemberRemoved = "member.removed";
    public const string MemberLeft = "member.left";
    public const string MemberClaimed = "member.claimed";
    public const string MemberRoleChanged = "member.role_changed";

    // group
    public const string GroupCreated = "group.created";
    public const string GroupRenamed = "group.renamed";
    public const string GroupArchived = "group.archived";
    public const string GroupUnarchived = "group.unarchived";

    // invite
    public const string InviteCreated = "invite.created";
    public const string InviteAccepted = "invite.accepted";
    public const string InviteRevoked = "invite.revoked";

    // group export (E4)
    public const string GroupExported = "group.exported";

    // recurring (E3)
    public const string RecurringMaterialized = "recurring.materialized";

    /// <summary>The full closed set (verification / test aid).</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        ExpenseCreated, ExpenseUpdated, ExpenseDeleted,
        SettlementRecorded, SettlementVoided,
        MemberAdded, MemberRemoved, MemberLeft, MemberClaimed, MemberRoleChanged,
        GroupCreated, GroupRenamed, GroupArchived, GroupUnarchived,
        InviteCreated, InviteAccepted, InviteRevoked,
        GroupExported,
        RecurringMaterialized,
    };
}

/// <summary>The <c>activity_log.target_type</c> value set (§3.10) — selects the live row to hydrate at read time.</summary>
public static class ActivityTargetTypes
{
    public const string Expense = "expense";
    public const string Settlement = "settlement";
    public const string Member = "member";
    public const string Group = "group";
    public const string Invite = "invite";
}

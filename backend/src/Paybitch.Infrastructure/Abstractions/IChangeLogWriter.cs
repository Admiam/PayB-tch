namespace Paybitch.Infrastructure.Abstractions;

/// <summary>The closed set of change_log.entity_type values (v1 + E3 recurring + E10a comments).</summary>
public static class ChangeLogEntityTypes
{
    public const string Group = "group";
    public const string Member = "member";
    public const string Expense = "expense";
    public const string Settlement = "settlement";
    public const string Category = "category";
    public const string Access = "access";               // synthetic — access grant/revoke
    public const string RecurringRule = "recurring_rule"; // E3
    public const string Comment = "comment";              // E10a

    /// <summary>Authoritative full value set for the DB CHECK (Appendix A; E1 'attachment' excluded).</summary>
    public static readonly IReadOnlyList<string> All =
        [Group, Member, Expense, Settlement, Category, Access, RecurringRule, Comment];
}

/// <summary>
/// Writes /sync change_log rows in the SAME transaction as the mutation (never a trigger — the
/// synthetic 'access' entry needs actor/membership context). Rows are added to the DbContext; the
/// caller owns the SaveChanges/transaction.
/// </summary>
public interface IChangeLogWriter
{
    /// <summary>Append a change row for a group-scoped entity mutation.</summary>
    void Append(Guid groupId, string entityType, Guid entityId, bool isDelete);

    /// <summary>Append a synthetic 'access' grant (isRevoke=false) or revoke (isRevoke=true) of a user's group visibility.</summary>
    void AppendAccess(Guid groupId, Guid userId, bool isRevoke);
}

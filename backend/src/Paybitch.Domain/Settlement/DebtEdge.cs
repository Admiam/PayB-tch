namespace Paybitch.Domain.Settlement;

/// <summary>
/// A single simplified transfer: <see cref="FromMemberId"/> pays <see cref="ToMemberId"/>
/// the given <see cref="Amount"/> (single currency).
/// </summary>
public sealed record DebtEdge(string FromMemberId, string ToMemberId, Money Amount);

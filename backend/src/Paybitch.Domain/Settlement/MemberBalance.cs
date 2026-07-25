namespace Paybitch.Domain.Settlement;

/// <summary>
/// A member's net position in a single currency. <c>Net &gt; 0</c> ⇒ the group owes the
/// member; <c>Net &lt; 0</c> ⇒ the member owes the group. Σ Net == 0 per currency, always.
/// </summary>
public sealed record MemberBalance(string MemberId, Money Net);

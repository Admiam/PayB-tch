//
//  BalanceCalculator.swift
//  Paybitch
//
//  Pure fn — no state. Net balance per member in target currency.
//  net > 0 → others owe member. net < 0 → member owes others.
//

import Foundation

struct MemberBalance: Hashable, Sendable, Identifiable {
    let memberId: String
    let net: Decimal
    var id: String { memberId }
}

enum BalanceCalculator {
    /// Net position per member, converted into `target`.
    ///
    /// **Invariant: the returned nets sum to zero.** Two things enforce it:
    ///
    /// 1. The payer is credited with what was *actually distributed*, not with the expense amount.
    ///    `SplitType.shares` guarantees those are equal for any non-empty split, so this is normally
    ///    the same number — but tying the credit to the debit makes the invariant hold by
    ///    construction rather than by assumption, including for malformed stored data.
    /// 2. Members who appear in the ledger but are no longer in `memberIds` are still reported.
    ///    Dropping them (the previous behaviour) silently deleted their debt and left the remaining
    ///    balances not summing to zero — the money someone owed simply vanished from the group.
    static func balances(
        expenses: [Expense],
        memberIds: [String],
        in target: Currency,
        fx: any FXProvider
    ) -> [MemberBalance] {
        var net: [String: Decimal] = Dictionary(uniqueKeysWithValues: memberIds.map { ($0, .zero) })
        // Preserves the caller's member order, then appends ledger-only members in first-seen order.
        var order = memberIds
        var known = Set(memberIds)

        func touch(_ id: String) {
            if known.insert(id).inserted { order.append(id) }
        }

        for e in expenses {
            let amount = fx.convert(e.amount, from: e.currency, to: target)
                .rounded(scale: target.decimals)
            let shares = e.splitType.shares(
                total: amount,
                among: e.splitAmong,
                roundedTo: target.decimals
            )

            let distributed = shares.values.reduce(Decimal.zero, +)
            touch(e.paidBy)
            net[e.paidBy, default: 0] += distributed

            for (m, s) in shares {
                touch(m)
                net[m, default: 0] -= s
            }
        }

        return order.map { MemberBalance(memberId: $0, net: net[$0] ?? 0) }
    }
}

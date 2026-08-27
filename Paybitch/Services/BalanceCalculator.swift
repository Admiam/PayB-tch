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
    static func balances(
        expenses: [Expense],
        memberIds: [String],
        in target: Currency,
        fx: any FXProvider
    ) -> [MemberBalance] {
        var net: [String: Decimal] = Dictionary(uniqueKeysWithValues: memberIds.map { ($0, .zero) })

        for e in expenses {
            let amount = fx.convert(e.amount, from: e.currency, to: target)
                .rounded(scale: target.decimals)
            net[e.paidBy, default: 0] += amount
            let shares = e.splitType.shares(
                total: amount,
                among: e.splitAmong,
                roundedTo: target.decimals
            )
            for (m, s) in shares { net[m, default: 0] -= s }
        }

        return memberIds.map { MemberBalance(memberId: $0, net: net[$0] ?? 0) }
    }
}

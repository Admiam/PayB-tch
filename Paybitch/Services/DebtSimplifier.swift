//
//  DebtSimplifier.swift
//  Paybitch
//
//  Greedy creditor↔debtor pairing. Minimal hop count, not provably optimal
//  for arbitrary graphs, but ≤ N-1 transactions.
//

import Foundation

struct DebtEdge: Hashable, Sendable, Identifiable {
    let from: String
    let to: String
    let amount: Decimal
    var id: String { "\(from)→\(to)" }
}

enum DebtSimplifier {
    static func simplify(
        _ balances: [MemberBalance],
        epsilon: Decimal = Decimal(string: "0.01") ?? 0
    ) -> [DebtEdge] {
        var creditors = balances.filter { $0.net > epsilon }
            .sorted { $0.net > $1.net }
            .map { (id: $0.memberId, amount: $0.net) }
        var debtors = balances.filter { $0.net < -epsilon }
            .sorted { $0.net < $1.net }
            .map { (id: $0.memberId, amount: -$0.net) }

        var edges: [DebtEdge] = []
        var i = 0, j = 0
        while i < creditors.count && j < debtors.count {
            let pay = Swift.min(creditors[i].amount, debtors[j].amount)
            edges.append(DebtEdge(from: debtors[j].id, to: creditors[i].id, amount: pay))
            creditors[i].amount -= pay
            debtors[j].amount -= pay
            if creditors[i].amount < epsilon { i += 1 }
            if debtors[j].amount < epsilon { j += 1 }
        }
        return edges
    }
}

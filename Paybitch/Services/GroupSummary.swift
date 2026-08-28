//
//  GroupSummary.swift
//  Paybitch
//
//  Everything the dashboard derives from one group's expenses, computed once.
//
//  Each of Tiles, Members, DebtSummaryList and ContentView used to derive this
//  independently, and Tiles reached `balances` three times within itself, so a
//  single render walked the expense list eight times. Measured in Debug on the
//  simulator that was ~31 ms at 200 expenses — nearly two dropped frames before
//  SwiftUI had laid anything out. Deriving it once and handing it down keeps
//  that to a single pass.
//

import Foundation

struct GroupSummary: Equatable, Sendable {
    let currency: Currency
    let expenses: [Expense]
    let balances: [MemberBalance]
    let edges: [DebtEdge]

    /// The current user's net position; zero when there is no current user.
    let myNet: Decimal
    /// The current user's own share across every expense.
    let myCosts: Decimal
    let totalCosts: Decimal

    var isEmpty: Bool { expenses.isEmpty }

    /// Nobody owes anybody, and there is something to owe on.
    var isSettled: Bool {
        !expenses.isEmpty && myNet.magnitude < Self.epsilon && edges.isEmpty
    }

    var iOwedRatio: Double {
        guard totalCosts > 0 else { return 0 }
        return (myNet.magnitude / totalCosts).doubleValue
    }

    var myCostsRatio: Double {
        guard totalCosts > 0 else { return 0 }
        return (myCosts / totalCosts).doubleValue
    }

    /// Sub-cent residue is not a debt anyone can settle.
    private static let epsilon = Decimal(string: "0.01") ?? 0

    static func make(
        group: Group,
        expenses allExpenses: [Expense],
        currentUserId: String?,
        fx: any FXProvider
    ) -> GroupSummary {
        let currency = Currency(rawValue: group.defaultCurrency.uppercased()) ?? .default
        let expenses = allExpenses.filter { $0.groupId == group.id }

        let balances = BalanceCalculator.balances(
            expenses: expenses,
            memberIds: group.memberIds,
            in: currency,
            fx: fx
        )
        let edges = DebtSimplifier.simplify(balances)
        let myNet = balances.first { $0.memberId == currentUserId }?.net ?? 0

        // One walk covers both totals; they used to be two separate passes.
        var myCosts = Decimal.zero
        var totalCosts = Decimal.zero
        for expense in expenses {
            let converted = fx
                .convert(expense.amount, from: expense.currency, to: currency)
                .rounded(scale: currency.decimals)
            totalCosts += converted

            guard let me = currentUserId else { continue }
            let shares = expense.splitType.shares(
                total: converted,
                among: expense.splitAmong,
                roundedTo: currency.decimals
            )
            myCosts += shares[me] ?? 0
        }

        return GroupSummary(
            currency: currency,
            expenses: expenses,
            balances: balances,
            edges: edges,
            myNet: myNet,
            myCosts: myCosts,
            totalCosts: totalCosts
        )
    }
}

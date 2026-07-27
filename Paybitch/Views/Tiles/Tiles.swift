//
//  Tiles.swift
//  Paybitch
//

import SwiftUI

struct Tiles: View {
    @Environment(ModelData.self) private var model
    let group: Group

    private var displayCurrency: Currency {
        Currency(rawValue: group.defaultCurrency.uppercased()) ?? .default
    }

    private var groupExpenses: [Expense] { model.expenses(forGroup: group.id) }

    private var balances: [MemberBalance] {
        BalanceCalculator.balances(
            expenses: groupExpenses,
            memberIds: group.memberIds,
            in: displayCurrency,
            fx: model.fx
        )
    }

    private var debts: [DebtEdge] {
        DebtSimplifier.simplify(balances, in: displayCurrency)
    }

    private var myNet: Decimal {
        guard let me = model.currentUserId else { return 0 }
        return balances.first { $0.memberId == me }?.net ?? 0
    }

    private var myCosts: Decimal {
        guard let me = model.currentUserId else { return 0 }
        return groupExpenses.reduce(.zero) { acc, e in
            let amt = model.fx.convert(e.amount, from: e.currency, to: displayCurrency)
                .rounded(scale: displayCurrency.decimals)
            let shares = e.splitType.shares(
                total: amt,
                among: e.splitAmong,
                roundedTo: displayCurrency.decimals
            )
            return acc + (shares[me] ?? 0)
        }
    }

    private var totalCosts: Decimal {
        groupExpenses.reduce(.zero) { acc, e in
            acc + model.fx.convert(e.amount, from: e.currency, to: displayCurrency)
                .rounded(scale: displayCurrency.decimals)
        }
    }

    private var iOwedRatio: Double {
        guard totalCosts > 0 else { return 0 }
        return (myNet.magnitude / totalCosts).doubleValue
    }

    private var myCostsRatio: Double {
        guard totalCosts > 0 else { return 0 }
        return (myCosts / totalCosts).doubleValue
    }

    private var iOwedSub: String? {
        guard let me = model.currentUserId else { return nil }
        if myNet > 0 {
            let n = debts.filter { $0.to == me }.count
            return "from \(n) \(n == 1 ? "person" : "people")"
        }
        if myNet < 0 {
            let n = debts.filter { $0.from == me }.count
            return "to \(n) \(n == 1 ? "person" : "people")"
        }
        return "all settled"
    }

    var body: some View {
        VStack(spacing: 10) {
            IOwed(net: myNet, currency: displayCurrency, sub: iOwedSub, ratio: iOwedRatio)
            HStack(spacing: 10) {
                MyCosts(value: myCosts, currency: displayCurrency, ratio: myCostsRatio)
                TotalCosts(value: totalCosts, currency: displayCurrency, expenseCount: groupExpenses.count)
            }
        }
    }
}

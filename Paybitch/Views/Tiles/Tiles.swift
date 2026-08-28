//
//  Tiles.swift
//  Paybitch
//

import SwiftUI

struct Tiles: View {
    @Environment(ModelData.self) private var model
    /// Derived once by the dashboard — see `GroupSummary`.
    let summary: GroupSummary

    private var iOwedSub: String? {
        guard let me = model.currentUserId else { return nil }
        if summary.myNet > 0 {
            let n = summary.edges.filter { $0.to == me }.count
            return "from \(n) \(n == 1 ? "person" : "people")"
        }
        if summary.myNet < 0 {
            let n = summary.edges.filter { $0.from == me }.count
            return "to \(n) \(n == 1 ? "person" : "people")"
        }
        return "all settled"
    }

    var body: some View {
        VStack(spacing: 10) {
            IOwed(
                net: summary.myNet,
                currency: summary.currency,
                sub: iOwedSub,
                ratio: summary.iOwedRatio
            )
            HStack(spacing: 10) {
                MyCosts(
                    value: summary.myCosts,
                    currency: summary.currency,
                    ratio: summary.myCostsRatio
                )
                TotalCosts(
                    value: summary.totalCosts,
                    currency: summary.currency,
                    expenseCount: summary.expenses.count
                )
            }
        }
    }
}

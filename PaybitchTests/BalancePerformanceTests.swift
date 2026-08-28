//
//  BalancePerformanceTests.swift
//  PaybitchTests
//
//  Measures what one dashboard render costs. The dashboard recomputes balances
//  from the full expense list in several places, so the per-call cost is
//  multiplied by however many of those run — see the comment on
//  `dashboardRenderCost` below.
//

import Foundation
import Testing
@testable import Paybitch

@Suite("Balance performance")
struct BalancePerformanceTests {

    /// A group of `memberCount` people sharing `expenseCount` expenses.
    private func fixture(
        expenseCount: Int,
        memberCount: Int = 5
    ) -> (expenses: [Expense], memberIds: [String]) {
        let memberIds = (0..<memberCount).map { "m\($0)" }
        let expenses = (0..<expenseCount).map { i in
            Expense(
                id: "e\(i)",
                groupId: "g1",
                title: "Item \(i)",
                amount: Decimal(100 + i),
                currency: .czk,
                paidBy: memberIds[i % memberCount],
                splitAmong: memberIds,
                splitType: .equal,
                date: Date(),
                notes: nil,
                iconSymbol: nil,
                createdAt: Date()
            )
        }
        return (expenses, memberIds)
    }

    /// Results go to a file: Swift Testing does not surface `print` in the
    /// xcodebuild log, and a benchmark nobody can read is not a benchmark.
    private func report(_ line: String) {
        let url = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("paybitch-perf.txt")
        let existing = (try? String(contentsOf: url, encoding: .utf8)) ?? ""
        try? (existing + line + "\n").write(to: url, atomically: true, encoding: .utf8)
    }

    private func measure(_ iterations: Int = 20, _ block: () -> Void) -> Double {
        // One warm pass so the first-call cost is not reported as the average.
        block()
        let start = Date()
        for _ in 0..<iterations { block() }
        return Date().timeIntervalSince(start) / Double(iterations) * 1000
    }

    @Test("one balances pass, by expense count")
    func balancesCost() {
        let fx = StaticFXProvider()
        for count in [10, 50, 200] {
            let (expenses, memberIds) = fixture(expenseCount: count)
            let ms = measure {
                _ = BalanceCalculator.balances(
                    expenses: expenses, memberIds: memberIds, in: .czk, fx: fx)
            }
            report("  balances over \(count) expenses: \(String(format: "%.2f", ms)) ms")
        }
    }

    /// What one dashboard render costs now that the derivation is shared.
    ///
    /// Before `GroupSummary`, Tiles reached `balances` three times on its own
    /// and walked the list twice more, while ContentView, DebtSummaryList and
    /// Members each derived it again — eight passes per render. This measures
    /// the single pass that replaced them, against that old shape.
    @Test("cost of one dashboard render")
    func dashboardRenderCost() {
        let fx = StaticFXProvider()

        for count in [10, 50, 200] {
            let (expenses, memberIds) = fixture(expenseCount: count)
            let group = Group(
                id: "g1", name: "G", memberIds: memberIds, defaultCurrency: "CZK")

            let shared = measure {
                _ = GroupSummary.make(
                    group: group,
                    expenses: expenses,
                    currentUserId: memberIds[0],
                    fx: fx
                )
            }

            // The shape the dashboard had before: six independent derivations
            // plus two extra full walks for the two totals.
            let perView = measure {
                for _ in 0..<6 {
                    let b = BalanceCalculator.balances(
                        expenses: expenses, memberIds: memberIds, in: .czk, fx: fx)
                    _ = DebtSimplifier.simplify(b)
                }
                for _ in 0..<2 {
                    _ = expenses.reduce(Decimal.zero) { acc, e in
                        let amt = fx.convert(e.amount, from: e.currency, to: .czk)
                            .rounded(scale: Currency.czk.decimals)
                        let shares = e.splitType.shares(
                            total: amt, among: e.splitAmong,
                            roundedTo: Currency.czk.decimals)
                        return acc + (shares[memberIds[0]] ?? 0)
                    }
                }
            }

            let budget = shared > 16.7 ? "  OVER a 60fps frame" : ""
            report(
                "  \(count) expenses — was \(String(format: "%.2f", perView)) ms, "
                + "now \(String(format: "%.2f", shared)) ms"
                + " (\(String(format: "%.1f", perView / max(shared, 0.001)))x)\(budget)"
            )

            // A render must fit inside a frame, or scrolling visibly stutters.
            #expect(shared < 16.7, "one render should fit in a 60fps frame")
        }
    }
}

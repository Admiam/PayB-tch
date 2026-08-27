//
//  BalanceCalculatorTests.swift
//  PaybitchTests
//

import Foundation
import Testing
@testable import Paybitch

private struct IdentityFX: FXProvider {
    func rate(from: Currency, to: Currency) -> Decimal { 1 }
}

private struct FixedFX: FXProvider {
    /// 1 EUR = 25 CZK; identity for everything else.
    func rate(from: Currency, to: Currency) -> Decimal {
        if from == to { return 1 }
        if from == .eur && to == .czk { return 25 }
        if from == .czk && to == .eur { return Decimal(string: "0.04")! }
        return 1
    }
}

private func expense(
    _ id: String = "e",
    paidBy: String,
    splitAmong: [String],
    amount: Decimal,
    splitType: SplitType = .equal,
    currency: Currency = .czk
) -> Expense {
    Expense(
        id: id,
        groupId: "g",
        title: id,
        amount: amount,
        currency: currency,
        paidBy: paidBy,
        splitAmong: splitAmong,
        splitType: splitType
    )
}

@Suite("BalanceCalculator.balances")
struct BalanceCalculatorTests {

    @Test("equal split: payer is owed the others' shares")
    func equalSplitPayerOwed() {
        let exps = [expense(paidBy: "a", splitAmong: ["a", "b", "c"], amount: 90)]
        let bs = BalanceCalculator.balances(
            expenses: exps,
            memberIds: ["a", "b", "c"],
            in: .czk,
            fx: IdentityFX()
        )
        let net = Dictionary(uniqueKeysWithValues: bs.map { ($0.memberId, $0.net) })
        // a: paid 90, owes 30 → net +60. b,c: -30 each.
        #expect(net["a"] == 60)
        #expect(net["b"] == -30)
        #expect(net["c"] == -30)
    }

    @Test("balances sum to zero exactly (no float drift)")
    func balancesSumToZero() {
        let exps = [
            expense("e1", paidBy: "a", splitAmong: ["a", "b", "c"], amount: 100),
            expense("e2", paidBy: "b", splitAmong: ["a", "b"], amount: 50),
        ]
        let bs = BalanceCalculator.balances(
            expenses: exps,
            memberIds: ["a", "b", "c"],
            in: .czk,
            fx: IdentityFX()
        )
        let total = bs.reduce(Decimal.zero) { $0 + $1.net }
        #expect(total == 0)
    }

    @Test("100 / 3 split closes exactly with no cent loss")
    func centLossEliminated() {
        let exps = [expense(paidBy: "a", splitAmong: ["a", "b", "c"], amount: 100)]
        let bs = BalanceCalculator.balances(
            expenses: exps,
            memberIds: ["a", "b", "c"],
            in: .eur,  // 2 decimals → 33.33 + 33.33 + 33.34 = 100
            fx: IdentityFX()
        )
        let total = bs.reduce(Decimal.zero) { $0 + $1.net }
        #expect(total == 0)
    }

    @Test("exact split honors per-member amounts")
    func exactSplit() {
        let split = SplitType.exact(["a": 60, "b": 40])
        let exps = [expense(paidBy: "a", splitAmong: ["a", "b"], amount: 100, splitType: split)]
        let bs = BalanceCalculator.balances(
            expenses: exps,
            memberIds: ["a", "b"],
            in: .czk,
            fx: IdentityFX()
        )
        let net = Dictionary(uniqueKeysWithValues: bs.map { ($0.memberId, $0.net) })
        #expect(net["a"] == 40)
        #expect(net["b"] == -40)
    }

    @Test("shares split divides by weights")
    func sharesSplit() {
        let split = SplitType.shares(["a": 1, "b": 3])
        let exps = [expense(paidBy: "a", splitAmong: ["a", "b"], amount: 100, splitType: split)]
        let bs = BalanceCalculator.balances(
            expenses: exps,
            memberIds: ["a", "b"],
            in: .czk,
            fx: IdentityFX()
        )
        let net = Dictionary(uniqueKeysWithValues: bs.map { ($0.memberId, $0.net) })
        #expect(net["a"] == 75)
        #expect(net["b"] == -75)
    }

    @Test("FX conversion converts to target currency")
    func fxConversion() {
        let exps = [expense(paidBy: "a", splitAmong: ["a", "b"], amount: 10, currency: .eur)]
        let bs = BalanceCalculator.balances(
            expenses: exps,
            memberIds: ["a", "b"],
            in: .czk,
            fx: FixedFX()
        )
        let net = Dictionary(uniqueKeysWithValues: bs.map { ($0.memberId, $0.net) })
        #expect(net["a"] == 125)
        #expect(net["b"] == -125)
    }

    @Test("opposite-direction expense cancels prior debt")
    func oppositeExpenseCancelsDebt() {
        // B paid 100 split among A,B → A owes B 50.
        let original = expense("orig", paidBy: "b", splitAmong: ["a", "b"], amount: 100)
        // A pays B back with a direct expense (no settle-up flag — just regular expense).
        let payback = expense("payback", paidBy: "a", splitAmong: ["b"], amount: 50)
        let bs = BalanceCalculator.balances(
            expenses: [original, payback],
            memberIds: ["a", "b"],
            in: .czk,
            fx: IdentityFX()
        )
        let net = Dictionary(uniqueKeysWithValues: bs.map { ($0.memberId, $0.net) })
        #expect(net["a"] == 0)
        #expect(net["b"] == 0)
    }

    @Test("missing member ids included with zero net")
    func missingMemberZeroNet() {
        let bs = BalanceCalculator.balances(
            expenses: [],
            memberIds: ["a", "b"],
            in: .czk,
            fx: IdentityFX()
        )
        #expect(bs.count == 2)
        #expect(bs.allSatisfy { $0.net == 0 })
    }
}

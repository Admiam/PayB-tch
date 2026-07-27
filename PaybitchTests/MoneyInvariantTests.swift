//
//  MoneyInvariantTests.swift
//  PaybitchTests
//
//  The properties that must never break, and the parity contract with the server.
//
//  The pre-existing suites were green while the money logic was wrong, because they only ever
//  exercised whole numbers through an identity FX provider — precisely the two dimensions the bugs
//  lived in. These tests cover the crossings: conversion × exact split, indivisible totals,
//  zero-decimal currencies, and member sets whose order is not stable.
//

import Foundation
import Testing
@testable import Paybitch

private struct IdentityFX: FXProvider {
    func rate(from: Currency, to: Currency) -> Decimal { 1 }
}

/// 1 EUR = 25 CZK; identity otherwise.
private struct FixedFX: FXProvider {
    func rate(from: Currency, to: Currency) -> Decimal {
        if from == to { return 1 }
        if from == .eur && to == .czk { return 25 }
        if from == .czk && to == .eur { return Decimal(string: "0.04")! }
        return 1
    }
}

private func dec(_ s: String) -> Decimal { Decimal(string: s)! }

private func expense(
    _ id: String = "e",
    paidBy: String,
    splitAmong: [String],
    amount: Decimal,
    splitType: SplitType = .equal,
    currency: Currency = .czk
) -> Expense {
    Expense(
        id: id, groupId: "g", title: id,
        amount: amount, currency: currency,
        paidBy: paidBy, splitAmong: splitAmong, splitType: splitType
    )
}

// MARK: - The invariant

@Suite("Money invariant: Σ net == 0")
struct MoneyInvariantTests {

    /// The regression that motivated all of this.
    ///
    /// Before the fix, `.exact` returned the stored amounts untouched while the payer was credited
    /// the *converted* total. A 100 EUR expense in a CZK group credited 2500 Kč and debited 60 + 40
    /// — inventing 2400 Kč out of nothing, silently, on every render.
    @Test("exact split under FX conversion still sums to zero")
    func exactSplitUnderFXBalances() {
        let split = SplitType.exact(["a": 60, "b": 40])
        let exps = [expense(paidBy: "a", splitAmong: ["a", "b"], amount: 100,
                            splitType: split, currency: .eur)]

        let bs = BalanceCalculator.balances(
            expenses: exps, memberIds: ["a", "b"], in: .czk, fx: FixedFX()
        )
        let net = Dictionary(uniqueKeysWithValues: bs.map { ($0.memberId, $0.net) })

        // 100 EUR → 2500 CZK, split 60:40 → 1500 / 1000.
        #expect(net["a"] == 1000)
        #expect(net["b"] == -1000)
        #expect(bs.reduce(Decimal.zero) { $0 + $1.net } == 0)
    }

    @Test("exact amounts that already reconcile pass through untouched")
    func exactSplitSameCurrencyPassesThrough() {
        let split = SplitType.exact(["a": dec("60.55"), "b": dec("39.45")])
        let shares = split.shares(total: 100, among: ["a", "b"], roundedTo: 2)
        #expect(shares["a"] == dec("60.55"))
        #expect(shares["b"] == dec("39.45"))
    }

    @Test("a shares split with no weights cannot invent money")
    func zeroWeightSharesStillBalances() {
        // Previously returned [:] — nobody was debited while the payer was credited in full.
        let split = SplitType.shares(["a": 0, "b": 0])
        let exps = [expense(paidBy: "a", splitAmong: ["a", "b"], amount: 100, splitType: split)]

        let bs = BalanceCalculator.balances(
            expenses: exps, memberIds: ["a", "b"], in: .czk, fx: IdentityFX()
        )
        #expect(bs.reduce(Decimal.zero) { $0 + $1.net } == 0)
    }

    @Test("a member removed from the group keeps their debt visible")
    func removedMemberStillReported() {
        // "c" was in the split but is no longer a group member. Dropping them used to delete the
        // debt outright and leave the remaining balances not summing to zero.
        let exps = [expense(paidBy: "a", splitAmong: ["a", "b", "c"], amount: 90)]
        let bs = BalanceCalculator.balances(
            expenses: exps, memberIds: ["a", "b"], in: .czk, fx: IdentityFX()
        )
        let net = Dictionary(uniqueKeysWithValues: bs.map { ($0.memberId, $0.net) })

        #expect(net["c"] == -30)
        #expect(bs.reduce(Decimal.zero) { $0 + $1.net } == 0)
    }

    @Test("invariant holds across a mixed-currency, indivisible ledger")
    func mixedLedgerBalances() {
        let exps = [
            expense("e1", paidBy: "a", splitAmong: ["a", "b", "c"], amount: 100, currency: .czk),
            expense("e2", paidBy: "b", splitAmong: ["a", "b", "c"], amount: dec("33.33"),
                    splitType: .shares(["a": 1, "b": 2, "c": 3]), currency: .eur),
            expense("e3", paidBy: "c", splitAmong: ["a", "c"], amount: dec("0.01"), currency: .eur),
            expense("e4", paidBy: "a", splitAmong: ["b"], amount: 7, currency: .czk),
        ]
        for target in Currency.allCases {
            let bs = BalanceCalculator.balances(
                expenses: exps, memberIds: ["a", "b", "c"], in: target, fx: FixedFX()
            )
            #expect(bs.reduce(Decimal.zero) { $0 + $1.net } == 0, "Σ net != 0 in \(target.rawValue)")
        }
    }
}

// MARK: - Parity with the server's Splitter

@Suite("Split parity with server Splitter")
struct SplitParityTests {

    // Cases lifted from backend/tests/Paybitch.Domain.Tests/SplitterTests.cs. The client preview
    // and the server's authoritative split must agree unit-for-unit, or amounts change after sync.

    @Test("EUR 100 among 3 → 33.34 / 33.33 / 33.33")
    func equal100EURAmong3() {
        let s = SplitType.equal.shares(total: 100, among: ["a", "b", "c"], roundedTo: 2)
        #expect(s["a"] == dec("33.34"))  // leftover unit to the first ordinal
        #expect(s["b"] == dec("33.33"))
        #expect(s["c"] == dec("33.33"))
        #expect(s.values.reduce(Decimal.zero, +) == 100)
    }

    @Test("CZK 100 among 3 → 34 / 33 / 33 (zero decimals)")
    func equal100CZKAmong3() {
        let s = SplitType.equal.shares(total: 100, among: ["a", "b", "c"], roundedTo: 0)
        #expect(s["a"] == 34)
        #expect(s["b"] == 33)
        #expect(s["c"] == 33)
        #expect(s.values.reduce(Decimal.zero, +) == 100)
    }

    @Test("one cent among 3 → exactly one member gets it")
    func oneCentAmong3() {
        let s = SplitType.equal.shares(total: dec("0.01"), among: ["a", "b", "c"], roundedTo: 2)
        #expect(s.values.reduce(Decimal.zero, +) == dec("0.01"))
        #expect(s.values.filter { $0 == dec("0.01") }.count == 1)
        #expect(s.values.filter { $0 == 0 }.count == 2)
        #expect(s["a"] == dec("0.01"))
    }

    @Test("spread is fair — max and min differ by at most one unit")
    func spreadIsFair() {
        let s = SplitType.equal.shares(total: dec("0.10"), among: ["a", "b", "c"], roundedTo: 2)
        let minors = s.values.map { $0 * 100 }
        #expect(s.values.reduce(Decimal.zero, +) == dec("0.10"))
        #expect(minors.max()! - minors.min()! <= 1)
        #expect(minors.max()! == 4)  // 4,3,3
    }

    /// The server gives the leftover unit to the smallest ordinal id regardless of array position.
    /// This is what makes the split independent of how `splitAmong` happens to be ordered — it is
    /// built from a `Set`, whose order is randomised per process.
    @Test("tie-break is by ordinal id, not by position")
    func tieBreakIsOrdinalNotPositional() {
        let s = SplitType.equal.shares(total: 100, among: ["z", "y", "x"], roundedTo: 2)
        #expect(s["x"] == dec("33.34"))
        #expect(s["y"] == dec("33.33"))
        #expect(s["z"] == dec("33.33"))
    }

    @Test("allocation is identical under every permutation of the members")
    func allocationIsOrderIndependent() {
        let reference = SplitType.equal.shares(total: 100, among: ["a", "b", "c"], roundedTo: 2)
        for permutation in [["a", "c", "b"], ["b", "a", "c"], ["b", "c", "a"],
                            ["c", "a", "b"], ["c", "b", "a"]] {
            let s = SplitType.equal.shares(total: 100, among: permutation, roundedTo: 2)
            #expect(s == reference, "order \(permutation) changed the allocation")
        }
    }

    @Test("repeated evaluation is stable")
    func repeatedEvaluationIsStable() {
        let members = ["m3", "m1", "m2"]
        let first = SplitType.shares(["m1": 1, "m2": 1, "m3": 1])
            .shares(total: 100, among: members, roundedTo: 2)
        for _ in 0..<50 {
            let again = SplitType.shares(["m1": 1, "m2": 1, "m3": 1])
                .shares(total: 100, among: members, roundedTo: 2)
            #expect(again == first)
        }
    }

    @Test("a duplicated member id claims only one share")
    func duplicateMemberClaimsOneShare() {
        let s = SplitType.equal.shares(total: 100, among: ["a", "b", "a"], roundedTo: 2)
        #expect(s.count == 2)
        #expect(s.values.reduce(Decimal.zero, +) == 100)
    }
}

// MARK: - Input parsing

@Suite("Decimal.parseUserInput")
struct ParseUserInputTests {

    @Test("plain forms")
    func plainForms() {
        #expect(Decimal.parseUserInput("100") == 100)
        #expect(Decimal.parseUserInput("100.5") == dec("100.5"))
        #expect(Decimal.parseUserInput("100,5") == dec("100.5"))
        #expect(Decimal.parseUserInput("-42") == -42)
    }

    /// The old implementation truncated every one of these to a smaller, plausible number —
    /// the worst failure mode an amount field can have.
    @Test("thousands separators are not swallowed")
    func thousandsSeparators() {
        #expect(Decimal.parseUserInput("1 234,56") == dec("1234.56"))       // space (cs)
        #expect(Decimal.parseUserInput("1\u{00A0}234,56") == dec("1234.56")) // NBSP
        #expect(Decimal.parseUserInput("1\u{202F}234,56") == dec("1234.56")) // narrow NBSP
        #expect(Decimal.parseUserInput("1,234.56") == dec("1234.56"))       // en-US
        #expect(Decimal.parseUserInput("1.234,56") == dec("1234.56"))       // de/cs
        #expect(Decimal.parseUserInput("1'234.56") == dec("1234.56"))       // de-CH
        #expect(Decimal.parseUserInput("1.234.567,89") == dec("1234567.89"))
    }

    @Test("garbage returns nil rather than a wrong number")
    func garbageIsRejected() {
        #expect(Decimal.parseUserInput("") == nil)
        #expect(Decimal.parseUserInput("abc") == nil)
        #expect(Decimal.parseUserInput("  ") == nil)
    }
}

// MARK: - Currency scale

@Suite("Currency epsilon")
struct CurrencyEpsilonTests {

    @Test("epsilon is half a minor unit for each currency")
    func epsilonMatchesMinorUnit() {
        #expect(Currency.czk.epsilon == dec("0.5"))
        #expect(Currency.eur.epsilon == dec("0.005"))
        #expect(Currency.usd.epsilon == dec("0.005"))
    }

    /// A fixed 0.01 threshold treated sub-koruna dust as a real debt and produced a phantom edge
    /// in a group that was in fact settled.
    @Test("CZK rounding dust does not become a settle-up edge")
    func czkDustIsNotADebt() {
        let balances = [
            MemberBalance(memberId: "a", net: dec("0.4")),
            MemberBalance(memberId: "b", net: dec("-0.4")),
        ]
        #expect(DebtSimplifier.simplify(balances, in: .czk).isEmpty)
        // The same dust is real money in EUR, where the minor unit is a hundredth.
        #expect(!DebtSimplifier.simplify(balances, in: .eur).isEmpty)
    }
}

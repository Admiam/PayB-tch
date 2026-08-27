//
//  DebtSimplifierTests.swift
//  PaybitchTests
//

import Foundation
import Testing
@testable import Paybitch

private func bal(_ id: String, _ net: Decimal) -> MemberBalance {
    MemberBalance(memberId: id, net: net)
}

@Suite("DebtSimplifier.simplify")
struct DebtSimplifierTests {

    @Test("balanced group produces no edges")
    func balancedNoEdges() {
        let edges = DebtSimplifier.simplify([bal("a", 0), bal("b", 0)])
        #expect(edges.isEmpty)
    }

    @Test("simple two-party debt produces one edge")
    func simpleDebt() {
        let edges = DebtSimplifier.simplify([bal("a", -50), bal("b", 50)])
        #expect(edges.count == 1)
        let e = edges[0]
        #expect(e.from == "a")
        #expect(e.to == "b")
        #expect(e.amount == 50)
    }

    @Test("three-party with one creditor produces two edges")
    func threePartyOneCreditor() {
        let edges = DebtSimplifier.simplify([
            bal("a", -30),
            bal("b", -20),
            bal("c", 50),
        ])
        #expect(edges.count == 2)
        let total = edges.reduce(Decimal.zero) { $0 + $1.amount }
        #expect(total == 50)
        #expect(edges.allSatisfy { $0.to == "c" })
    }

    @Test("edges count is at most N-1")
    func atMostNMinusOneEdges() {
        let edges = DebtSimplifier.simplify([
            bal("a", -10),
            bal("b", -20),
            bal("c", 5),
            bal("d", 25),
        ])
        #expect(edges.count <= 3)
        let credit = edges.reduce(Decimal.zero) { $0 + $1.amount }
        #expect(credit == 30)
    }

    @Test("sub-epsilon balances are ignored")
    func epsilonIgnored() {
        let edges = DebtSimplifier.simplify([
            bal("a", Decimal(string: "-0.005")!),
            bal("b", Decimal(string: "0.005")!),
        ])
        #expect(edges.isEmpty)
    }

    @Test("largest creditor matched first")
    func largestCreditorFirst() {
        let edges = DebtSimplifier.simplify([
            bal("a", -80),
            bal("b", 30),
            bal("c", 50),
        ])
        #expect(edges.first?.from == "a")
        #expect(edges.first?.to == "c")
    }

    @Test("preserves total credit equals total debit")
    func conservation() {
        let balances = [
            bal("a", Decimal(string: "-45.50")!),
            bal("b", Decimal(string: "-14.50")!),
            bal("c", 30),
            bal("d", 30),
        ]
        let edges = DebtSimplifier.simplify(balances)
        let edgeTotal = edges.reduce(Decimal.zero) { $0 + $1.amount }
        let creditTotal = balances.filter { $0.net > 0 }.reduce(Decimal.zero) { $0 + $1.net }
        #expect(edgeTotal == creditTotal)
    }
}

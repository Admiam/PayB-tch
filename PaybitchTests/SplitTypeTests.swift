//
//  SplitTypeTests.swift
//  PaybitchTests
//

import Foundation
import Testing
@testable import Paybitch

@Suite("SplitType.shares")
struct SplitTypeTests {

    @Test("equal split divides total evenly when divisible")
    func equalSplitDividesEvenly() {
        let result = SplitType.equal.shares(total: 90, among: ["a", "b", "c"])
        #expect(result == ["a": 30, "b": 30, "c": 30])
    }

    @Test("equal split sum equals total exactly (no cent loss)")
    func equalSplitNoCentLoss() {
        let total: Decimal = 100
        let members = ["a", "b", "c"]
        let result = SplitType.equal.shares(total: total, among: members, roundedTo: 2)
        let sum = result.values.reduce(.zero, +)
        #expect(sum == total)
        // Per-share rounded to 33.33 except last which absorbs the remainder.
        #expect(result["a"] == Decimal(string: "33.33"))
        #expect(result["b"] == Decimal(string: "33.33"))
        #expect(result["c"] == Decimal(string: "33.34"))
    }

    @Test("equal split with empty members returns empty")
    func equalEmptyMembers() {
        let result = SplitType.equal.shares(total: 100, among: [])
        #expect(result.isEmpty)
    }

    @Test("exact split returns provided amounts")
    func exactSplit() {
        let map: [String: Decimal] = ["a": 70, "b": 30]
        let result = SplitType.exact(map).shares(total: 100, among: ["a", "b"])
        #expect(result["a"] == 70)
        #expect(result["b"] == 30)
    }

    @Test("exact split returns 0 for missing member")
    func exactMissingMember() {
        let map: [String: Decimal] = ["a": 100]
        let result = SplitType.exact(map).shares(total: 100, among: ["a", "b"])
        #expect(result["a"] == 100)
        #expect(result["b"] == 0)
    }

    @Test("shares split divides by weight")
    func sharesSplit() {
        let weights: [String: Int] = ["a": 1, "b": 1, "c": 2]
        let result = SplitType.shares(weights).shares(total: 100, among: ["a", "b", "c"])
        #expect(result["a"] == 25)
        #expect(result["b"] == 25)
        #expect(result["c"] == 50)
    }

    @Test("shares with zero total weight returns empty")
    func sharesZeroWeight() {
        let weights: [String: Int] = ["a": 0, "b": 0]
        let result = SplitType.shares(weights).shares(total: 100, among: ["a", "b"])
        #expect(result.isEmpty)
    }

    @Test("shares ignores members with weight 0")
    func sharesPartialZero() {
        let weights: [String: Int] = ["a": 1, "b": 0, "c": 1]
        let result = SplitType.shares(weights).shares(total: 100, among: ["a", "b", "c"])
        #expect(result["a"] == 50)
        #expect(result["b"] == 0)
        #expect(result["c"] == 50)
    }

    @Test("shares split sum equals total exactly even with rounding")
    func sharesNoCentLoss() {
        let weights: [String: Int] = ["a": 1, "b": 1, "c": 1]
        let result = SplitType.shares(weights).shares(total: 100, among: ["a", "b", "c"], roundedTo: 2)
        let sum = result.values.reduce(.zero, +)
        #expect(sum == 100)
    }

    @Test("Codable round-trip preserves cases")
    func splitTypeCodable() throws {
        let cases: [SplitType] = [
            .equal,
            .exact(["a": 10, "b": 20]),
            .shares(["a": 1, "b": 2]),
        ]
        let enc = JSONEncoder()
        let dec = JSONDecoder()
        for c in cases {
            let data = try enc.encode(c)
            let back = try dec.decode(SplitType.self, from: data)
            #expect(back == c)
        }
    }
}

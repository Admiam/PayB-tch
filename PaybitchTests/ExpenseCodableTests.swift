//
//  ExpenseCodableTests.swift
//  PaybitchTests
//

import Foundation
import Testing
@testable import Paybitch

@Suite("Expense Codable")
struct ExpenseCodableTests {

    @Test("round-trip preserves all fields")
    func roundTrip() throws {
        let original = Expense(
            id: "abc",
            groupId: "g",
            title: "Lunch",
            amount: Decimal(string: "123.45")!,
            currency: .eur,
            paidBy: "u1",
            splitAmong: ["u1", "u2"],
            splitType: .shares(["u1": 1, "u2": 2]),
            date: PaybitchDate.parseISO("2026-01-15")!,
            notes: "Italian place",
            createdAt: Date(timeIntervalSince1970: 1_700_000_000)
        )
        let data = try JSONEncoder().encode(original)
        let decoded = try JSONDecoder().decode(Expense.self, from: data)
        #expect(decoded == original)
    }

    @Test("decodes legacy JSON with date as yyyy-MM-dd string and amount as Double")
    func legacyDecode() throws {
        let json = """
        {
          "id": "1",
          "groupId": "g",
          "title": "Coffee",
          "amount": 4.5,
          "currency": "EUR",
          "paidBy": "u1",
          "splitAmong": ["u1","u2"],
          "splitType": {"equal":{}},
          "date": "2025-12-01",
          "createdAt": 0
        }
        """.data(using: .utf8)!
        let decoded = try JSONDecoder().decode(Expense.self, from: json)
        #expect(decoded.title == "Coffee")
        #expect(decoded.amount == Decimal(string: "4.5"))
        #expect(decoded.currency == .eur)
        #expect(PaybitchDate.isoDay.string(from: decoded.date) == "2025-12-01")
    }

    @Test("decodes JSON with missing optional fields")
    func toleratesMissingFields() throws {
        // No splitType, no notes, no createdAt, no currency.
        let json = """
        {
          "id": "1",
          "groupId": "g",
          "title": "X",
          "amount": 100,
          "paidBy": "u1",
          "splitAmong": ["u1"],
          "date": "2025-01-01"
        }
        """.data(using: .utf8)!
        let decoded = try JSONDecoder().decode(Expense.self, from: json)
        #expect(decoded.splitType == .equal)
        #expect(decoded.currency == .czk) // default
        #expect(decoded.notes == nil)
    }

    @Test("legacy isSettlement field is silently ignored")
    func legacySettlementFieldIgnored() throws {
        let json = """
        {
          "id": "1",
          "groupId": "g",
          "title": "Old payback",
          "amount": 50,
          "currency": "CZK",
          "paidBy": "u1",
          "splitAmong": ["u2"],
          "splitType": {"equal":{}},
          "date": "2025-01-01",
          "createdAt": 0,
          "isSettlement": true
        }
        """.data(using: .utf8)!
        let decoded = try JSONDecoder().decode(Expense.self, from: json)
        #expect(decoded.title == "Old payback")
        #expect(decoded.amount == 50)
    }
}

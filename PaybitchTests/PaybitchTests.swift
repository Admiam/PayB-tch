//
//  PaybitchTests.swift
//  PaybitchTests
//
//  Created by Adam Míka on 01.08.2025.
//

import Testing
@testable import Paybitch

@MainActor
struct PaybitchTests {

    @Test func loadsSeedDataFromBundle() {
        let model = ModelData()
        #expect(model.groups.count == 3)
        #expect(model.members.count == 6)
        #expect(model.expenses.count == 8)
    }

    @Test func groupLookupByIDReturnsMatch() {
        let model = ModelData()
        #expect(model.group(for: "g1")?.name == "Spain Trip 2025")
        #expect(model.group(for: "g2")?.name == "Roommates")
        #expect(model.group(for: "missing") == nil)
    }

    @Test func memberLookupByIDReturnsMatch() {
        let model = ModelData()
        #expect(model.member(for: "u1")?.name == "Alice")
        #expect(model.member(for: "u6")?.name == "Frank")
        #expect(model.member(for: "missing") == nil)
    }

    @Test(
        "Expense filtering returns only the requested group's expenses",
        arguments: [("g1", 3), ("g2", 3), ("g3", 2), ("missing", 0)]
    )
    func expensesAreFilteredByGroup(groupID: String, expectedCount: Int) {
        let model = ModelData()
        let expenses = model.expenses(forGroup: groupID)
        #expect(expenses.count == expectedCount)
        #expect(expenses.allSatisfy { $0.groupId == groupID })
    }
}

//
//  PaybitchTests.swift
//  PaybitchTests
//
//  Created by Adam Míka on 01.08.2025.
//
//  These used to construct a bare `ModelData()` and assert on demo content
//  shipped in the bundle — "Spain Trip 2025", "Alice", eight expenses. The seed
//  files were emptied to `[]` before release and the assertions were never
//  updated, so the suite had been failing on four tests since then.
//
//  Rewritten to supply their own fixtures through the repository protocols,
//  matching ModelDataTests. That also removes a race: `ModelData.init` starts
//  an unawaited refresh, so reading it synchronously was never reliable.
//

import Foundation
import Testing
@testable import Paybitch

private actor FixtureExpenseRepo: ExpenseRepository {
    private var store: [Expense]
    init(seed: [Expense] = []) { self.store = seed }
    func listAll() async throws -> [Expense] { store }
    func list(groupId: String) async throws -> [Expense] {
        store.filter { $0.groupId == groupId }
    }
    func create(_ e: Expense) async throws -> Expense { store.append(e); return e }
    func update(_ e: Expense) async throws -> Expense { e }
    func delete(id: String) async throws { store.removeAll { $0.id == id } }
}

private actor FixtureMemberRepo: MemberRepository {
    private var store: [Member]
    init(seed: [Member] = []) { self.store = seed }
    func list() async throws -> [Member] { store }
    func create(_ m: Member) async throws -> Member { store.append(m); return m }
    func update(_ m: Member) async throws -> Member { m }
    func delete(id: String) async throws { store.removeAll { $0.id == id } }
}

private actor FixtureGroupRepo: GroupRepository {
    private var store: [Group]
    init(seed: [Group] = []) { self.store = seed }
    func list() async throws -> [Group] { store }
    func create(_ g: Group) async throws -> Group { store.append(g); return g }
    func update(_ g: Group) async throws -> Group { g }
    func delete(id: String) async throws { store.removeAll { $0.id == id } }
}

private final class FixtureUserStore: CurrentUserStore, @unchecked Sendable {
    private let lock = NSLock()
    private var value: String?
    init(initial: String? = nil) { self.value = initial }
    func get() -> String? { lock.withLock { value } }
    func set(_ id: String?) { lock.withLock { value = id } }
}

/// The shape the old bundle seed used to have, so the assertions still mean
/// the same thing — they just no longer depend on what ships in the app.
private enum Fixture {
    static let members: [Member] = [
        Member(id: "u1", name: "Alice"),
        Member(id: "u2", name: "Bob"),
        Member(id: "u3", name: "Cara"),
        Member(id: "u4", name: "Dan"),
        Member(id: "u5", name: "Eve"),
        Member(id: "u6", name: "Frank"),
    ]

    static let groups: [Group] = [
        Group(id: "g1", name: "Spain Trip 2025",
              memberIds: ["u1", "u2", "u3"], defaultCurrency: "EUR"),
        Group(id: "g2", name: "Roommates",
              memberIds: ["u1", "u4"], defaultCurrency: "CZK"),
        Group(id: "g3", name: "Ski 2026",
              memberIds: ["u5", "u6"], defaultCurrency: "CZK"),
    ]

    /// Three in g1, three in g2, two in g3.
    static let expenses: [Expense] = {
        let counts = [("g1", 3), ("g2", 3), ("g3", 2)]
        return counts.flatMap { groupId, count in
            (0..<count).map { i in
                Expense(
                    id: "\(groupId)-e\(i)",
                    groupId: groupId,
                    title: "Item \(i)",
                    amount: Decimal(100 + i),
                    currency: .czk,
                    paidBy: "u1",
                    splitAmong: ["u1", "u2"],
                    splitType: .equal,
                    date: Date(),
                    notes: nil,
                    iconSymbol: nil,
                    createdAt: Date()
                )
            }
        }
    }()
}

@MainActor
private func makeModel() async -> ModelData {
    let model = ModelData(
        expenseRepo: FixtureExpenseRepo(seed: Fixture.expenses),
        memberRepo: FixtureMemberRepo(seed: Fixture.members),
        groupRepo: FixtureGroupRepo(seed: Fixture.groups),
        fx: StaticFXProvider(),
        userStore: FixtureUserStore(initial: "u1")
    )
    // init fires an unawaited refresh; wait for a deterministic state.
    await model.refresh()
    return model
}

@MainActor
struct PaybitchTests {

    @Test func loadsEverythingTheRepositoriesHold() async {
        let model = await makeModel()
        #expect(model.groups.count == 3)
        #expect(model.members.count == 6)
        #expect(model.expenses.count == 8)
    }

    @Test func groupLookupByIDReturnsMatch() async {
        let model = await makeModel()
        #expect(model.group(for: "g1")?.name == "Spain Trip 2025")
        #expect(model.group(for: "g2")?.name == "Roommates")
        #expect(model.group(for: "missing") == nil)
    }

    @Test func memberLookupByIDReturnsMatch() async {
        let model = await makeModel()
        #expect(model.member(for: "u1")?.name == "Alice")
        #expect(model.member(for: "u6")?.name == "Frank")
        #expect(model.member(for: "missing") == nil)
    }

    @Test(
        "Expense filtering returns only the requested group's expenses",
        arguments: [("g1", 3), ("g2", 3), ("g3", 2), ("missing", 0)]
    )
    func expensesAreFilteredByGroup(groupID: String, expectedCount: Int) async {
        let model = await makeModel()
        let expenses = model.expenses(forGroup: groupID)
        #expect(expenses.count == expectedCount)
        #expect(expenses.allSatisfy { $0.groupId == groupID })
    }
}

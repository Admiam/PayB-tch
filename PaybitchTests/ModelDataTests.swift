//
//  ModelDataTests.swift
//  PaybitchTests
//
//  Behavioural tests around ModelData with in-memory repos.
//

import Foundation
import Testing
@testable import Paybitch

private actor InMemoryExpenseRepo: ExpenseRepository {
    private var store: [Expense] = []
    func listAll() async throws -> [Expense] { store }
    func list(groupId: String) async throws -> [Expense] { store.filter { $0.groupId == groupId } }
    func create(_ e: Expense) async throws -> Expense { store.append(e); return e }
    func update(_ e: Expense) async throws -> Expense {
        guard let idx = store.firstIndex(where: { $0.id == e.id }) else {
            throw NSError(domain: "test", code: 404)
        }
        store[idx] = e
        return e
    }
    func delete(id: String) async throws { store.removeAll { $0.id == id } }
}

private actor InMemoryMemberRepo: MemberRepository {
    private var store: [Member]
    init(seed: [Member] = []) { self.store = seed }
    func list() async throws -> [Member] { store }
    func create(_ m: Member) async throws -> Member { store.append(m); return m }
    func update(_ m: Member) async throws -> Member {
        guard let idx = store.firstIndex(where: { $0.id == m.id }) else {
            throw NSError(domain: "test", code: 404)
        }
        store[idx] = m
        return m
    }
    func delete(id: String) async throws { store.removeAll { $0.id == id } }
}

private actor InMemoryGroupRepo: GroupRepository {
    private var store: [Group]
    init(seed: [Group] = []) { self.store = seed }
    func list() async throws -> [Group] { store }
    func create(_ g: Group) async throws -> Group { store.append(g); return g }
    func update(_ g: Group) async throws -> Group {
        guard let idx = store.firstIndex(where: { $0.id == g.id }) else {
            throw NSError(domain: "test", code: 404)
        }
        store[idx] = g
        return g
    }
    func delete(id: String) async throws { store.removeAll { $0.id == id } }
}

private final class InMemoryUserStore: CurrentUserStore, @unchecked Sendable {
    private let lock = NSLock()
    private var value: String?
    init(initial: String? = nil) { self.value = initial }
    func get() -> String? { lock.withLock { value } }
    func set(_ id: String?) { lock.withLock { value = id } }
}

@MainActor
private func makeModel(
    members: [Member] = [],
    groups: [Group] = [],
    currentUserId: String? = nil
) async -> ModelData {
    let model = ModelData(
        expenseRepo: InMemoryExpenseRepo(),
        memberRepo: InMemoryMemberRepo(seed: members),
        groupRepo: InMemoryGroupRepo(seed: groups),
        fx: StaticFXProvider(),
        userStore: InMemoryUserStore(initial: currentUserId)
    )
    // Init kicks off Task { await refresh() } — wait for it.
    await model.refresh()
    return model
}

@MainActor
@Suite("ModelData behaviour")
struct ModelDataTests {

    @Test("creates Me member when none exists")
    func createsMeWhenAbsent() async {
        let model = await makeModel()
        let me = model.currentUser
        #expect(me != nil)
        #expect(me?.name == "Me")
        #expect(model.currentUserId == me?.id)
    }

    @Test("reuses existing Me when present in repo")
    func reusesExistingMe() async {
        let existing = Member(id: "existing-me", name: "Me", imageUrl: nil)
        let model = await makeModel(members: [existing])
        #expect(model.currentUserId == "existing-me")
    }

    @Test("backfills current user into all groups on refresh")
    func backfillsMeIntoGroups() async {
        let me = Member(id: "me", name: "Me", imageUrl: nil)
        let g1 = Group(id: "g1", name: "Trip", memberIds: ["other"], defaultCurrency: "CZK")
        let g2 = Group(id: "g2", name: "Roommates", memberIds: ["me", "other"], defaultCurrency: "CZK")
        let model = await makeModel(members: [me], groups: [g1, g2], currentUserId: "me")

        let live1 = model.group(for: "g1")
        let live2 = model.group(for: "g2")
        #expect(live1?.memberIds.contains("me") == true)
        #expect(live2?.memberIds.contains("me") == true)
        // No duplicates in g2.
        #expect(live2?.memberIds.filter { $0 == "me" }.count == 1)
    }

    @Test("adding an expense updates expenses array")
    func addExpenseUpdatesState() async {
        let me = Member(id: "me", name: "Me", imageUrl: nil)
        let g = Group(id: "g", name: "G", memberIds: ["me"], defaultCurrency: "CZK")
        let model = await makeModel(members: [me], groups: [g], currentUserId: "me")

        let e = Expense(
            groupId: "g",
            title: "Coffee",
            amount: 50,
            paidBy: "me",
            splitAmong: ["me"]
        )
        await model.addExpense(e)

        #expect(model.expenses.count == 1)
        #expect(model.expenses(forGroup: "g").count == 1)
    }

    @Test("delete group cascades to its expenses")
    func deleteGroupCascades() async {
        let me = Member(id: "me", name: "Me", imageUrl: nil)
        let g = Group(id: "g", name: "G", memberIds: ["me"], defaultCurrency: "CZK")
        let model = await makeModel(members: [me], groups: [g], currentUserId: "me")
        await model.addExpense(Expense(
            groupId: "g", title: "x", amount: 10, paidBy: "me", splitAmong: ["me"]
        ))
        #expect(model.expenses.count == 1)

        await model.deleteGroup(id: "g")
        #expect(model.groups.isEmpty)
        #expect(model.expenses.isEmpty)
    }

    @Test("displayName returns Me for current user, name otherwise")
    func displayNameRules() async {
        let me = Member(id: "me", name: "Adam", imageUrl: nil)
        let other = Member(id: "o", name: "Bob", imageUrl: nil)
        let model = await makeModel(members: [me, other], currentUserId: "me")
        #expect(model.displayName(for: "me") == "Me")
        #expect(model.displayName(for: "o") == "Bob")
        #expect(model.displayName(for: "missing") == "—")
    }

    @Test("setCurrentUser persists via userStore and backfills groups")
    func setCurrentUserBackfills() async {
        let a = Member(id: "a", name: "A", imageUrl: nil)
        let b = Member(id: "b", name: "B", imageUrl: nil)
        let g = Group(id: "g", name: "G", memberIds: ["a"], defaultCurrency: "CZK")
        let model = await makeModel(members: [a, b], groups: [g], currentUserId: "a")
        #expect(model.group(for: "g")?.memberIds == ["a"])

        model.setCurrentUser("b")
        // The backfill is fire-and-forget Task; let it run.
        try? await Task.sleep(nanoseconds: 50_000_000)
        #expect(model.group(for: "g")?.memberIds.contains("b") == true)
    }
}

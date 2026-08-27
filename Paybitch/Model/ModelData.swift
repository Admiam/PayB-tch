//
//  ModelData.swift
//  Paybitch
//

import Foundation
import Observation
import OSLog

private let logger = PaybitchLog.make("ModelData")

@MainActor
@Observable
final class ModelData {

    private(set) var expenses: [Expense] = []
    private(set) var members:  [Member]  = []
    private(set) var groups:   [Group]   = []

    var currentUserId: String?

    /// Surface for transient error toasts. View binds via `errorBinding`.
    var lastError: String?

    let expenseRepo: any ExpenseRepository
    let memberRepo: any MemberRepository
    let groupRepo: any GroupRepository
    let fx: any FXProvider
    private let userStore: any CurrentUserStore

    init(
        expenseRepo: any ExpenseRepository = LocalExpenseRepository(),
        memberRepo: any MemberRepository = LocalMemberRepository(),
        groupRepo: any GroupRepository = LocalGroupRepository(),
        fx: any FXProvider = StaticFXProvider(),
        userStore: any CurrentUserStore = UserDefaultsCurrentUserStore()
    ) {
        self.expenseRepo = expenseRepo
        self.memberRepo = memberRepo
        self.groupRepo = groupRepo
        self.fx = fx
        self.userStore = userStore
        self.currentUserId = userStore.get()
        Task { await self.refresh() }
    }

    func refresh() async {
        do {
            async let exp = expenseRepo.listAll()
            async let mem = memberRepo.list()
            async let grp = groupRepo.list()
            expenses = try await exp
            members  = try await mem
            groups   = try await grp
            await ensureMeMember()
            await ensureCurrentUserInAllGroups()
        } catch {
            report("Couldn't load data", error)
        }
    }

    /// Logs and surfaces a user-visible error message.
    private func report(_ summary: String, _ error: Error) {
        logger.error("\(summary): \(error.localizedDescription)")
        lastError = "\(summary). \(error.localizedDescription)"
    }

    func clearError() { lastError = nil }

    /// Guarantees a "Me" member exists and is set as currentUser when no profile is configured.
    private func ensureMeMember() async {
        if let id = currentUserId, member(for: id) != nil { return }
        // Reuse a previously-created Me if present, else create one.
        if let existing = members.first(where: { $0.name.caseInsensitiveCompare("Me") == .orderedSame }) {
            setCurrentUser(existing.id)
            return
        }
        let me = Member(id: UUID().uuidString, name: "Me", imageUrl: nil)
        if let saved = await addMember(me) {
            setCurrentUser(saved.id)
        }
    }

    /// Backfills current user into every group. Cheap and idempotent; runs after refresh
    /// and after profile switch so the active "Me" always appears in member rosters.
    private func ensureCurrentUserInAllGroups() async {
        guard let me = currentUserId else { return }
        for g in groups where !g.memberIds.contains(me) {
            var copy = g
            copy.memberIds.append(me)
            await updateGroup(copy)
        }
    }

    // MARK: - Current user

    func setCurrentUser(_ id: String?) {
        currentUserId = id
        userStore.set(id)
        Task { await ensureCurrentUserInAllGroups() }
    }

    var currentUser: Member? {
        guard let id = currentUserId else { return nil }
        return member(for: id)
    }

    // MARK: - Expenses

    @discardableResult
    func addExpense(_ expense: Expense) async -> Expense? {
        do {
            let saved = try await expenseRepo.create(expense)
            expenses.append(saved)
            return saved
        } catch {
            report("Couldn't save expense", error)
            return nil
        }
    }

    @discardableResult
    func updateExpense(_ expense: Expense) async -> Expense? {
        do {
            let saved = try await expenseRepo.update(expense)
            if let idx = expenses.firstIndex(where: { $0.id == saved.id }) {
                expenses[idx] = saved
            }
            return saved
        } catch {
            report("Couldn't update expense", error)
            return nil
        }
    }

    func deleteExpense(id: String) async {
        do {
            try await expenseRepo.delete(id: id)
            expenses.removeAll { $0.id == id }
        } catch {
            report("Couldn't delete expense", error)
        }
    }

    // MARK: - Members

    @discardableResult
    func addMember(_ member: Member) async -> Member? {
        do {
            let saved = try await memberRepo.create(member)
            members.append(saved)
            return saved
        } catch {
            report("Couldn't add member", error)
            return nil
        }
    }

    func updateMember(_ member: Member) async {
        do {
            let saved = try await memberRepo.update(member)
            if let idx = members.firstIndex(where: { $0.id == saved.id }) {
                members[idx] = saved
            }
        } catch {
            report("Couldn't update member", error)
        }
    }

    func deleteMember(id: String) async {
        do {
            try await memberRepo.delete(id: id)
            members.removeAll { $0.id == id }
            for g in groups where g.memberIds.contains(id) {
                var copy = g
                copy.memberIds.removeAll { $0 == id }
                await updateGroup(copy)
            }
            if currentUserId == id { setCurrentUser(members.first?.id) }
        } catch {
            report("Couldn't delete member", error)
        }
    }

    // MARK: - Groups

    @discardableResult
    func addGroup(_ group: Group) async -> Group? {
        do {
            let saved = try await groupRepo.create(group)
            groups.append(saved)
            return saved
        } catch {
            report("Couldn't create group", error)
            return nil
        }
    }

    func updateGroup(_ group: Group) async {
        do {
            let saved = try await groupRepo.update(group)
            if let idx = groups.firstIndex(where: { $0.id == saved.id }) {
                groups[idx] = saved
            }
        } catch {
            report("Couldn't update group", error)
        }
    }

    func deleteGroup(id: String) async {
        do {
            try await groupRepo.delete(id: id)
            groups.removeAll { $0.id == id }
            for e in expenses where e.groupId == id {
                await deleteExpense(id: e.id)
            }
        } catch {
            report("Couldn't delete group", error)
        }
    }

    // MARK: - Lookups

    func group(for id: String) -> Group? { groups.first { $0.id == id } }
    func member(for id: String) -> Member? { members.first { $0.id == id } }
    func expenses(forGroup id: String) -> [Expense] { expenses.filter { $0.groupId == id } }

    /// Returns "Me" for the current user, otherwise the member's name. Used in pickers/lists.
    func displayName(for memberId: String) -> String {
        guard let m = member(for: memberId) else { return "—" }
        return memberId == currentUserId ? "Me" : m.name
    }
}

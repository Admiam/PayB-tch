//
//  ActivityFeedView.swift
//  Paybitch
//
//  Chronological per-group history. Days as section headers; rows show
//  the expense and the user's net impact on it.
//

import SwiftUI

struct ActivityFeedView: View {
    @Environment(ModelData.self) private var model
    let groupId: String

    @State private var detailExpense: Expense?
    @State private var search: String = ""

    private var group: Group? { model.group(for: groupId) }

    private var filteredExpenses: [Expense] {
        let all = model.expenses.filter { $0.groupId == groupId }
        let needle = search.trimmingCharacters(in: .whitespaces).lowercased()
        guard !needle.isEmpty else { return all }
        return all.filter { e in
            if e.title.lowercased().contains(needle) { return true }
            if (e.notes ?? "").lowercased().contains(needle) { return true }
            let payer = model.member(for: e.paidBy)?.name.lowercased() ?? ""
            return payer.contains(needle)
        }
    }

    private var sections: [DaySection] {
        let exps = filteredExpenses.sorted { $0.createdAt > $1.createdAt }
        let grouped: [Date: [Expense]] = Dictionary(grouping: exps) { e in
            Calendar.current.startOfDay(for: e.date)
        }
        return grouped
            .map { DaySection(day: $0.key, expenses: $0.value) }
            .sorted { $0.day > $1.day }
    }

    var body: some View {
        SwiftUI.Group {
            if sections.isEmpty {
                ContentUnavailableView(
                    "No activity yet",
                    systemImage: "clock",
                    description: Text("Add an expense to see history here.")
                )
                .foregroundStyle(Paybitch.textPrimary)
            } else {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 14, pinnedViews: .sectionHeaders) {
                        ForEach(sections) { section in
                            Section {
                                VStack(spacing: 8) {
                                    ForEach(section.expenses) { e in
                                        Button { detailExpense = e } label: {
                                            ActivityRow(expense: e)
                                        }
                                        .buttonStyle(.plain)
                                    }
                                }
                                .padding(.horizontal, 16)
                            } header: {
                                sectionHeader(section.day)
                            }
                        }
                    }
                    .padding(.bottom, 60)
                }
                .background(Paybitch.bg.ignoresSafeArea())
            }
        }
        .paybitchBackground()
        .navigationTitle(group?.name ?? "Activity")
        .navigationBarTitleDisplayMode(.inline)
        .searchable(text: $search, prompt: Text("Search title, notes, payer"))
        .sheet(item: $detailExpense) { e in
            ExpenseDetailView(expenseId: e.id).presentationDetents([.large])
        }
    }

    private func sectionHeader(_ d: Date) -> some View {
        HStack(spacing: 8) {
            Text(formatSectionDate(d))
                .font(.spaceGrotesk(13, weight: .heavy))
                .tracking(0.6)
                .textCase(.uppercase)
                .foregroundStyle(Paybitch.pink)
            Capsule().fill(Paybitch.divider).frame(height: 1)
        }
        .padding(.horizontal, 16)
        .padding(.top, 12)
        .background(Paybitch.bg)
    }

    private func formatSectionDate(_ d: Date) -> String {
        if Calendar.current.isDateInToday(d) { return String(localized: "Today") }
        if Calendar.current.isDateInYesterday(d) { return String(localized: "Yesterday") }
        return PaybitchDate.mediumDisplay.string(from: d)
    }
}

private struct DaySection: Identifiable {
    let day: Date
    let expenses: [Expense]
    var id: Date { day }
}

private struct ActivityRow: View {
    @Environment(ModelData.self) private var model
    let expense: Expense

    private var meShare: Decimal {
        guard let me = model.currentUserId else { return 0 }
        let shares = expense.splitType.shares(
            total: expense.amount,
            among: expense.splitAmong,
            roundedTo: expense.currency.decimals
        )
        return shares[me] ?? 0
    }

    private var mePaid: Decimal {
        expense.paidBy == model.currentUserId ? expense.amount : 0
    }

    private var meNet: Decimal { mePaid - meShare }
    private var paidByMe: Bool { expense.paidBy == model.currentUserId }

    var body: some View {
        HStack(spacing: 12) {
            ZStack {
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .fill(paidByMe ? Paybitch.pink : Paybitch.chipBg)
                Image(systemName: PaybitchIcon.symbol(for: expense))
                    .font(.system(size: 20, weight: .semibold))
                    .foregroundStyle(paidByMe ? .white : Paybitch.pink)
            }
            .frame(width: 44, height: 44)

            VStack(alignment: .leading, spacing: 2) {
                Text(expense.title)
                    .font(.spaceGrotesk(15, weight: .bold))
                    .foregroundStyle(Paybitch.textPrimary)
                Text("\(model.displayName(for: expense.paidBy)) · \(expense.amount.formatted(in: expense.currency))")
                    .font(.spaceGrotesk(12, weight: .semibold))
                    .foregroundStyle(Paybitch.textMuted)
                if model.currentUserId != nil, meNet != 0 {
                    Text(meNetLabel)
                        .font(.spaceGrotesk(11, weight: .bold))
                        .foregroundStyle(meNet >= 0 ? Paybitch.positive : Paybitch.negative)
                }
            }

            Spacer()

            Text(relativeTime)
                .font(.spaceGrotesk(11, weight: .bold))
                .foregroundStyle(Paybitch.textMuted)
        }
        .padding(14)
        .background(
            RoundedRectangle(cornerRadius: Paybitch.radiusRow, style: .continuous)
                .fill(Paybitch.card)
        )
    }

    private var meNetLabel: String {
        if meNet > 0 { return "you're owed \(meNet.formatted(in: expense.currency))" }
        if meNet < 0 { return "you owe \(meNet.magnitude.formatted(in: expense.currency))" }
        return ""
    }

    private var relativeTime: String {
        PaybitchDate.relative.localizedString(for: expense.createdAt, relativeTo: .now)
    }
}

//
//  DebtsList.swift
//  Paybitch
//
//  Activity feed — list of expenses for the active group, sorted by date.
//  Tapping a row opens the expense detail sheet.
//

import SwiftUI

struct DebtsList: View {
    @Environment(ModelData.self) private var model
    var group: Group?

    @State private var detailExpense: Expense?

    private var groupExpenses: [Expense] {
        guard let group else { return [] }
        return model.expenses
            .filter { $0.groupId == group.id }
            .sorted { ($0.date, $0.createdAt) > ($1.date, $1.createdAt) }
    }

    var body: some View {
        if !groupExpenses.isEmpty {
            VStack(alignment: .leading, spacing: 0) {
                PaybitchSectionHeader(title: "Activity", badge: "\(groupExpenses.count)")
                    .padding(.horizontal, -8)

                VStack(spacing: 8) {
                    ForEach(groupExpenses) { expense in
                        Button {
                            detailExpense = expense
                        } label: {
                            ExpenseRow(expense: expense)
                        }
                        .buttonStyle(.plain)
                        .swipeActions(edge: .trailing) {
                            Button(role: .destructive) {
                                Task { await model.deleteExpense(id: expense.id) }
                            } label: {
                                Label("Delete", systemImage: "trash")
                            }
                        }
                    }
                }
            }
            .sheet(item: $detailExpense) { expense in
                ExpenseDetailView(expenseId: expense.id)
                    .presentationDetents([.large])
            }
        }
    }
}

private struct ExpenseRow: View {
    @Environment(ModelData.self) private var model
    let expense: Expense

    private var paidByMe: Bool { expense.paidBy == model.currentUserId }

    var body: some View {
        HStack(spacing: 12) {
            ZStack {
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .fill(paidByMe ? Paybitch.pink : Paybitch.chipBg)
                Image(systemName: PaybitchIcon.symbol(for: expense))
                    .font(.system(size: 22, weight: .semibold))
                    .foregroundStyle(paidByMe ? .white : Paybitch.pink)
            }
            .frame(width: 44, height: 44)

            VStack(alignment: .leading, spacing: 2) {
                Text(expense.title)
                    .font(.spaceGrotesk(15, weight: .bold))
                    .foregroundStyle(Paybitch.textPrimary)
                    .lineLimit(1)
                    .truncationMode(.tail)
                HStack(spacing: 4) {
                    Text("Paid by")
                        .foregroundStyle(Paybitch.textMuted)
                    Text(paidByMe ? "you" : model.displayName(for: expense.paidBy))
                        .foregroundStyle(Paybitch.textPrimary.opacity(0.85))
                        .fontWeight(.bold)
                    Text("·")
                        .foregroundStyle(Paybitch.textMuted)
                    Text(PaybitchDate.mediumDisplay.string(from: expense.date))
                        .foregroundStyle(Paybitch.textMuted)
                }
                .font(.spaceGrotesk(12, weight: .semibold))
            }

            Spacer()

            VStack(alignment: .trailing, spacing: 2) {
                Text(expense.amount.formatted(in: expense.currency))
                    .font(.spaceGrotesk(17, weight: .heavy))
                    .tracking(-0.3)
                    .foregroundStyle(Paybitch.textPrimary)
                    .monospacedDigit()
                Text("÷ \(expense.splitAmong.count)")
                    .font(.spaceGrotesk(10, weight: .heavy))
                    .tracking(0.4)
                    .foregroundStyle(Paybitch.textMuted)
                    .textCase(.uppercase)
            }
        }
        .padding(14)
        .frame(maxWidth: .infinity)
        .background(
            RoundedRectangle(cornerRadius: Paybitch.radiusRow, style: .continuous)
                .fill(Paybitch.card)
        )
    }
}

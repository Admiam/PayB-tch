//
//  ExpenseDetailView.swift
//  Paybitch
//
//  Read-only detail of a single expense. Edit → opens AddExpenseSheet.
//

import SwiftUI

struct ExpenseDetailView: View {
    @Environment(ModelData.self) private var model
    @Environment(\.dismiss) private var dismiss

    let expenseId: String

    @State private var editing = false
    @State private var showDeleteConfirm = false

    private var expense: Expense? {
        model.expenses.first { $0.id == expenseId }
    }

    private var group: Group? {
        guard let groupId = expense?.groupId else { return nil }
        return model.group(for: groupId)
    }

    private var sharesMap: [String: Decimal] {
        guard let e = expense else { return [:] }
        return e.splitType.shares(
            total: e.amount,
            among: e.splitAmong,
            roundedTo: e.currency.decimals
        )
    }

    var body: some View {
        NavigationStack {
            SwiftUI.Group {
                if let e = expense {
                    ScrollView { content(for: e) }
                        .background(Paybitch.bg.ignoresSafeArea())
                } else {
                    ContentUnavailableView("Expense not found", systemImage: "questionmark.circle")
                }
            }
            .navigationTitle("")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    PaybitchCloseButton { dismiss() }
                }
                ToolbarItem(placement: .primaryAction) {
                    if expense != nil {
                        PaybitchTextButton(title: "Edit") { editing = true }
                    }
                }
            }
            .sheet(isPresented: $editing) {
                if let e = expense, let g = group {
                    AddExpenseSheet(group: g, editing: e)
                        .presentationDetents([.large])
                }
            }
            .confirmationDialog(
                "Delete this expense?",
                isPresented: $showDeleteConfirm,
                titleVisibility: .visible
            ) {
                Button("Delete", role: .destructive) {
                    Task {
                        if let id = expense?.id {
                            await model.deleteExpense(id: id)
                            dismiss()
                        }
                    }
                }
                Button("Cancel", role: .cancel) {}
            }
        }
    }

    @ViewBuilder
    private func content(for e: Expense) -> some View {
        VStack(spacing: 18) {
            heroBlock(for: e)

            PaybitchFieldGroup(label: "Paid by") {
                paidByRow(for: e)
            }

            PaybitchFieldGroup(label: "Split between · \(e.splitAmong.count) \(e.splitAmong.count == 1 ? "person" : "people")") {
                VStack(spacing: 0) {
                    ForEach(Array(e.splitAmong.enumerated()), id: \.element) { idx, id in
                        if let m = model.member(for: id) {
                            splitRow(member: m, expense: e, isLast: idx == e.splitAmong.count - 1)
                        }
                    }
                }
            }

            if let notes = e.notes, !notes.isEmpty {
                PaybitchFieldGroup(label: "Notes") {
                    Text(notes)
                        .font(.spaceGrotesk(15))
                        .foregroundStyle(Paybitch.textPrimary)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(14)
                }
            }

            PaybitchDestructiveButton(title: "Delete expense") {
                showDeleteConfirm = true
            }
        }
        .padding(.horizontal, 16)
        .padding(.bottom, 60)
    }

    @ViewBuilder
    private func heroBlock(for e: Expense) -> some View {
        VStack(spacing: 12) {
            ZStack {
                RoundedRectangle(cornerRadius: 28, style: .continuous).fill(Paybitch.pink)
                Image(systemName: PaybitchIcon.symbol(for: e))
                    .font(.system(size: 44, weight: .semibold))
                    .foregroundStyle(.white)
            }
            .frame(width: 88, height: 88)
            .rotationEffect(.degrees(-4))
            .paybitchShadow(Paybitch.Shadow.pinkGlow)

            Text(e.title)
                .font(.spaceGrotesk(24, weight: .bold))
                .tracking(-0.5)
                .foregroundStyle(Paybitch.textPrimary)
                .multilineTextAlignment(.center)
                .padding(.top, 4)

            HStack(alignment: .firstTextBaseline, spacing: 4) {
                Text(e.amount.amountString(in: e.currency))
                    .font(.spaceGrotesk(56, weight: .heavy))
                    .tracking(-2)
                Text(e.currency.symbol)
                    .font(.spaceGrotesk(26, weight: .bold))
                    .opacity(0.5)
            }
            .foregroundStyle(Paybitch.textPrimary)
            .lineLimit(1)
            .minimumScaleFactor(0.6)

            HStack(spacing: 6) {
                StickerBadge(text: splitKindLabel(e.splitType.kind), rotation: -3)
                StickerBadge(text: PaybitchDate.mediumDisplay.string(from: e.date), rotation: 3)
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 12)
    }

    @ViewBuilder
    private func paidByRow(for e: Expense) -> some View {
        if let payer = model.member(for: e.paidBy) {
            HStack(spacing: 12) {
                PaybitchAvatar(member: payer, size: 40, isMe: model.currentUserId == payer.id)
                VStack(alignment: .leading, spacing: 2) {
                    Text(model.displayName(for: payer.id))
                        .font(.spaceGrotesk(16, weight: .bold))
                        .foregroundStyle(Paybitch.textPrimary)
                    Text("put up the whole tab")
                        .font(.spaceGrotesk(12))
                        .foregroundStyle(Paybitch.textMuted)
                }
                Spacer()
                Text("+\(e.amount.formatted(in: e.currency))")
                    .font(.spaceGrotesk(18, weight: .heavy))
                    .foregroundStyle(Paybitch.positive)
                    .monospacedDigit()
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 14)
        }
    }

    @ViewBuilder
    private func splitRow(member m: Member, expense e: Expense, isLast: Bool) -> some View {
        let share = sharesMap[m.id] ?? 0
        let isPayer = m.id == e.paidBy
        VStack(spacing: 0) {
            HStack(spacing: 12) {
                PaybitchAvatar(member: m, size: 36, isMe: model.currentUserId == m.id)
                Text(model.displayName(for: m.id))
                    .font(.spaceGrotesk(15, weight: .bold))
                    .foregroundStyle(Paybitch.textPrimary)
                Spacer()
                Text(isPayer ? "0\u{00A0}\(e.currency.symbol)" : "−\(share.formatted(in: e.currency))")
                    .font(.spaceGrotesk(14, weight: .bold))
                    .foregroundStyle(isPayer ? Paybitch.textMuted : Paybitch.negative)
                    .monospacedDigit()
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
            if !isLast { Divider().background(Paybitch.divider) }
        }
    }

    private func splitKindLabel(_ k: SplitType.Kind) -> String {
        switch k {
        case .equal: "equal"
        case .exact: "exact"
        case .shares: "shares"
        }
    }
}

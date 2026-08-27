//
//  AddExpenseSheet.swift
//  Paybitch
//
//  Create or edit an expense. Pass `editing` to load an existing record.
//

import SwiftUI

struct AddExpenseSheet: View {
    @Environment(ModelData.self) private var model
    @Environment(\.dismiss) private var dismiss

    let group: Group
    /// nil = create flow, non-nil = edit existing expense.
    let editing: Expense?

    @State private var title: String = ""
    @State private var amountText: String = ""
    @State private var currency: Currency = .default
    @State private var paidBy: String = ""
    @State private var splitAmong: Set<String> = []
    @State private var splitKind: SplitType.Kind = .equal
    @State private var exactAmounts: [String: Decimal] = [:]
    @State private var shareWeights: [String: Int] = [:]
    @State private var date: Date = .now
    @State private var notes: String = ""
    @State private var iconSymbol: String?
    @State private var showIconPicker = false
    @State private var saving = false
    @State private var showDeleteConfirm = false

    private var resolvedIcon: String {
        iconSymbol ?? PaybitchIcon.symbol(forTitle: title)
    }

    init(group: Group, editing: Expense? = nil) {
        self.group = group
        self.editing = editing
    }

    private var liveGroup: Group { model.group(for: group.id) ?? group }
    private var groupMembers: [Member] {
        liveGroup.memberIds.compactMap { model.member(for: $0) }
    }

    private var amount: Decimal? { Decimal.parseUserInput(amountText) }

    private var splitValid: Bool {
        guard !splitAmong.isEmpty else { return false }
        switch splitKind {
        case .equal: return true
        case .exact:
            guard let a = amount else { return false }
            let sum = splitAmong.reduce(Decimal.zero) { $0 + (exactAmounts[$1] ?? 0) }
            return (a - sum).magnitude < Decimal(string: "0.01")!
        case .shares:
            return splitAmong.contains { (shareWeights[$0] ?? 0) > 0 }
        }
    }

    private var canSave: Bool {
        !title.trimmingCharacters(in: .whitespaces).isEmpty
            && (amount ?? 0) > 0
            && !paidBy.isEmpty
            && splitValid
            && !saving
    }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: 18) {
                    heroCard

                    PaybitchFieldGroup(label: "Paid by") {
                        ScrollView(.horizontal, showsIndicators: false) {
                            HStack(spacing: 8) {
                                ForEach(groupMembers) { m in
                                    paidByPick(m)
                                }
                            }
                            .padding(12)
                        }
                    }

                    PaybitchFieldGroup(label: "Split between") {
                        VStack(spacing: 0) {
                            SplitEditor(
                                members: groupMembers,
                                total: amount ?? 0,
                                currency: currency,
                                splitKind: $splitKind,
                                selectedIds: $splitAmong,
                                exact: $exactAmounts,
                                shares: $shareWeights
                            )
                            .padding(14)
                        }
                    }

                    PaybitchFieldGroup(label: "Date · notes") {
                        VStack(spacing: 0) {
                            HStack(spacing: 12) {
                                Image(systemName: "calendar")
                                    .foregroundStyle(Paybitch.pink)
                                DatePicker("Date", selection: $date, displayedComponents: .date)
                                    .labelsHidden()
                                    .tint(Paybitch.pink)
                                Spacer()
                            }
                            .padding(14)
                            Divider().background(Paybitch.divider)
                            TextField("Notes (optional)", text: $notes, axis: .vertical)
                                .paybitchNoAutocorrect()
                                .font(.spaceGrotesk(15))
                                .foregroundStyle(Paybitch.textPrimary)
                                .lineLimit(1...4)
                                .padding(14)
                        }
                    }

                    if editing != nil {
                        PaybitchDestructiveButton(title: "Delete expense") {
                            showDeleteConfirm = true
                        }
                    }
                }
                .padding(.horizontal, 16)
                .padding(.bottom, 60)
            }
            .background(Paybitch.bg.ignoresSafeArea())
            .paybitchAppearance()
            .navigationTitle(editing == nil ? "New expense" : "Edit expense")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    PaybitchCloseButton { dismiss() }
                }
                .sharedBackgroundVisibility(.hidden)
                ToolbarItem(placement: .confirmationAction) {
                    PaybitchPrimaryButton(title: "Save", disabled: !canSave) {
                        Task { await save() }
                    }
                }
                .sharedBackgroundVisibility(.hidden)
            }
            .task { setDefaults() }
            .confirmationDialog(
                "Delete this expense?",
                isPresented: $showDeleteConfirm,
                titleVisibility: .visible
            ) {
                Button("Delete", role: .destructive) { Task { await deleteExpense() } }
                Button("Cancel", role: .cancel) {}
            }
            .sensoryFeedback(.success, trigger: saving) { _, new in !new }
            .sheet(isPresented: $showIconPicker) {
                IconPickerSheet(selection: $iconSymbol, titleHint: title)
                    .presentationDetents([.large])
            }
        }
    }

    private var heroCard: some View {
        VStack(spacing: 12) {
            HStack(spacing: 10) {
                Button {
                    showIconPicker = true
                } label: {
                    ZStack(alignment: .bottomTrailing) {
                        Image(systemName: resolvedIcon)
                            .font(.system(size: 22, weight: .semibold))
                            .foregroundStyle(.white)
                            .frame(width: 44, height: 44)
                            .background(
                                RoundedRectangle(cornerRadius: 14, style: .continuous)
                                    .fill(.black.opacity(0.2))
                            )
                        Image(systemName: "pencil.circle.fill")
                            .font(.system(size: 16))
                            .foregroundStyle(.white)
                            .background(Circle().fill(Paybitch.pink))
                            .offset(x: 4, y: 4)
                    }
                }
                .buttonStyle(.plain)
                .accessibilityLabel(Text("Choose icon"))
                TextField(text: $title, prompt: Text("What was it for?").foregroundStyle(.white.opacity(0.7))) {
                    Text("")
                }
                .paybitchNoAutocorrect()
                .font(.spaceGrotesk(15, weight: .bold))
                .foregroundStyle(.white)
                .padding(.horizontal, 14)
                .padding(.vertical, 10)
                .background(
                    RoundedRectangle(cornerRadius: 12, style: .continuous)
                        .fill(.black.opacity(0.18))
                )
            }

            HStack(alignment: .firstTextBaseline, spacing: 6) {
                TextField(text: $amountText, prompt: Text("0").foregroundStyle(.white.opacity(0.7))) {
                    Text("")
                }
                .keyboardType(.decimalPad)
                .font(.spaceGrotesk(56, weight: .heavy))
                .tracking(-2)
                .foregroundStyle(.white)
                .lineLimit(1)
                .minimumScaleFactor(0.5)

                Menu {
                    ForEach(Currency.allCases) { c in
                        Button(c.label) { currency = c }
                    }
                } label: {
                    Text(currency.symbol)
                        .font(.spaceGrotesk(22, weight: .bold))
                        .foregroundStyle(.white.opacity(0.7))
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(20)
        .background(
            RoundedRectangle(cornerRadius: 26, style: .continuous)
                .fill(LinearGradient(
                    colors: [Paybitch.pink, Color(red: 1.0, green: 0.482, blue: 0.714)],
                    startPoint: .topLeading, endPoint: .bottomTrailing
                ))
        )
    }

    @ViewBuilder
    private func paidByPick(_ m: Member) -> some View {
        let selected = paidBy == m.id
        Button {
            paidBy = m.id
        } label: {
            VStack(spacing: 6) {
                PaybitchAvatar(member: m, size: 40, isMe: model.currentUserId == m.id)
                Text(model.displayName(for: m.id))
                    .font(.spaceGrotesk(12, weight: .bold))
                    .foregroundStyle(selected ? Paybitch.pink : Paybitch.textPrimary)
            }
            .frame(minWidth: 64)
            .padding(.horizontal, 10)
            .padding(.vertical, 8)
            .overlay(
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .strokeBorder(selected ? Paybitch.pink : Color.clear, lineWidth: 2)
            )
        }
        .buttonStyle(.plain)
        .animation(.snappy(duration: 0.2), value: selected)
    }

    private func setDefaults() {
        if let editing { applyEditing(editing) } else { applyCreateDefaults() }
    }

    private func applyEditing(_ e: Expense) {
        title = e.title
        amountText = formatAmountForEdit(e.amount, currency: e.currency)
        currency = e.currency
        paidBy = e.paidBy
        splitAmong = Set(e.splitAmong)
        splitKind = e.splitType.kind
        switch e.splitType {
        case .equal: break
        case .exact(let map): exactAmounts = map
        case .shares(let map): shareWeights = map
        }
        for id in liveGroup.memberIds where shareWeights[id] == nil {
            shareWeights[id] = 1
        }
        date = e.date
        notes = e.notes ?? ""
        iconSymbol = e.iconSymbol
    }

    private func applyCreateDefaults() {
        let g = liveGroup
        currency = (Currency(rawValue: g.defaultCurrency.uppercased())) ?? .default
        let validIds = Set(g.memberIds)
        if paidBy.isEmpty || !validIds.contains(paidBy) {
            if let me = model.currentUserId, validIds.contains(me) {
                paidBy = me
            } else {
                paidBy = g.memberIds.first ?? ""
            }
        }
        if splitAmong.isEmpty { splitAmong = validIds }
        for id in g.memberIds where shareWeights[id] == nil {
            shareWeights[id] = 1
        }
    }

    private func formatAmountForEdit(_ value: Decimal, currency: Currency) -> String {
        let f = NumberFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.numberStyle = .decimal
        f.usesGroupingSeparator = false
        f.minimumFractionDigits = 0
        f.maximumFractionDigits = currency.decimals
        return f.string(from: value as NSDecimalNumber) ?? ""
    }

    private func save() async {
        guard let amount, !saving else { return }
        saving = true
        defer { saving = false }

        let split: SplitType = {
            switch splitKind {
            case .equal: return .equal
            case .exact:
                let map = splitAmong.reduce(into: [String: Decimal]()) { acc, id in
                    acc[id] = exactAmounts[id] ?? 0
                }
                return .exact(map)
            case .shares:
                let map = splitAmong.reduce(into: [String: Int]()) { acc, id in
                    acc[id] = shareWeights[id] ?? 0
                }
                return .shares(map)
            }
        }()

        let trimmedTitle = title.trimmingCharacters(in: .whitespaces)
        let trimmedNotes = notes.isEmpty ? nil : notes

        if let editing {
            let updated = Expense(
                id: editing.id,
                groupId: editing.groupId,
                title: trimmedTitle,
                amount: amount,
                currency: currency,
                paidBy: paidBy,
                splitAmong: Array(splitAmong),
                splitType: split,
                date: date,
                notes: trimmedNotes,
                iconSymbol: iconSymbol,
                createdAt: editing.createdAt
            )
            await model.updateExpense(updated)
        } else {
            let new = Expense(
                groupId: group.id,
                title: trimmedTitle,
                amount: amount,
                currency: currency,
                paidBy: paidBy,
                splitAmong: Array(splitAmong),
                splitType: split,
                date: date,
                notes: trimmedNotes,
                iconSymbol: iconSymbol
            )
            await model.addExpense(new)
        }
        dismiss()
    }

    private func deleteExpense() async {
        guard let editing else { return }
        await model.deleteExpense(id: editing.id)
        dismiss()
    }
}

//
//  SplitEditor.swift
//  Paybitch
//
//  Editor for SplitType. Equal = preview only. Exact = per-member amount.
//  Shares = per-member integer weight; effective amount shown.
//

import SwiftUI

struct SplitEditor: View {
    @Environment(ModelData.self) private var model

    let members: [Member]
    let total: Decimal
    let currency: Currency
    @Binding var splitKind: SplitType.Kind
    @Binding var selectedIds: Set<String>
    @Binding var exact: [String: Decimal]
    @Binding var shares: [String: Int]

    private var selected: [Member] {
        members.filter { selectedIds.contains($0.id) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Picker("Split type", selection: $splitKind) {
                ForEach(SplitType.Kind.allCases) { k in
                    Text(k.label).tag(k)
                }
            }
            .pickerStyle(.segmented)

            switch splitKind {
            case .equal:  equalView
            case .exact:  exactView
            case .shares: sharesView
            }
        }
    }

    // MARK: - Equal

    private var equalView: some View {
        VStack(alignment: .leading, spacing: 6) {
            ForEach(members) { m in
                memberToggleRow(m, trailing: equalTrailing(for: m))
            }
        }
    }

    @ViewBuilder
    private func equalTrailing(for m: Member) -> some View {
        if selectedIds.contains(m.id), !selected.isEmpty, total > 0 {
            let share = (total / Decimal(selected.count)).rounded(scale: currency.decimals)
            Text(share.formattedFull(in: currency))
                .font(.caption).foregroundStyle(.secondary).monospacedDigit()
        } else {
            EmptyView()
        }
    }

    // MARK: - Exact

    private var exactSum: Decimal {
        selected.reduce(.zero) { $0 + (exact[$1.id] ?? 0) }
    }

    private var exactDelta: Decimal { total - exactSum }

    private var exactView: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Enter exact amount each person owes. Sum must equal total.")
                .font(.caption)
                .foregroundStyle(.secondary)

            ForEach(members) { m in
                memberToggleRow(m, trailing: exactField(for: m))
            }

            HStack {
                Text("Sum")
                Spacer()
                Text(exactSum.formattedFull(in: currency))
                    .monospacedDigit()
                    .foregroundStyle(deltaColor)
            }
            .font(.subheadline)
            HStack {
                Text(exactDelta == 0 ? "Balanced" : exactDelta > 0 ? "Missing" : "Overshoot")
                Spacer()
                Text(exactDelta.magnitude.formattedFull(in: currency))
                    .monospacedDigit()
            }
            .font(.caption)
            .foregroundStyle(deltaColor)
        }
    }

    private var deltaColor: Color {
        exactDelta.magnitude < currency.epsilon ? .green : .red
    }

    @ViewBuilder
    private func exactField(for m: Member) -> some View {
        if selectedIds.contains(m.id) {
            TextField("0", value: Binding(
                get: { exact[m.id] ?? 0 },
                set: { exact[m.id] = $0 }
            ), format: .number)
            .keyboardType(.decimalPad)
            .multilineTextAlignment(.trailing)
            .frame(width: 100)
            .monospacedDigit()
        } else {
            EmptyView()
        }
    }

    // MARK: - Shares

    private var totalShares: Int {
        selected.reduce(0) { $0 + (shares[$1.id] ?? 0) }
    }

    private var sharesView: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Assign shares (e.g. 1 / 1 / 2 means last person pays double).")
                .font(.caption)
                .foregroundStyle(.secondary)

            ForEach(members) { m in
                memberToggleRow(m, trailing: shareControls(for: m))
            }

            HStack {
                Text("Total shares")
                Spacer()
                Text("\(totalShares)").monospacedDigit()
            }
            .font(.subheadline)
        }
    }

    @ViewBuilder
    private func shareControls(for m: Member) -> some View {
        if selectedIds.contains(m.id) {
            let value = shares[m.id] ?? 1
            HStack(spacing: 8) {
                if totalShares > 0, total > 0 {
                    let portion = (total * Decimal(value) / Decimal(totalShares))
                        .rounded(scale: currency.decimals)
                    Text(portion.formattedFull(in: currency))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .monospacedDigit()
                }
                Stepper(value: Binding(
                    get: { shares[m.id] ?? 1 },
                    set: { shares[m.id] = max(0, $0) }
                ), in: 0...20) {
                    Text("\(value)").monospacedDigit().frame(minWidth: 24)
                }
                .labelsHidden()
            }
        } else {
            EmptyView()
        }
    }

    // MARK: - Toggle row

    @ViewBuilder
    private func memberToggleRow<Trailing: View>(_ m: Member, trailing: Trailing) -> some View {
        HStack(spacing: 12) {
            Toggle(isOn: Binding(
                get: { selectedIds.contains(m.id) },
                set: { on in
                    if on {
                        selectedIds.insert(m.id)
                        if shares[m.id] == nil { shares[m.id] = 1 }
                    } else {
                        selectedIds.remove(m.id)
                    }
                }
            )) {
                HStack(spacing: 10) {
                    MemberIcon(member: m, size: 32)
                    Text(model.displayName(for: m.id))
                        .fontWeight(model.currentUserId == m.id ? .bold : .regular)
                }
            }
            .toggleStyle(.checkbox)

            Spacer()
            trailing
        }
    }
}

// MARK: - ToggleStyle iOS-friendly

private struct CheckboxToggleStyle: ToggleStyle {
    func makeBody(configuration: Configuration) -> some View {
        Button {
            configuration.isOn.toggle()
        } label: {
            HStack {
                Image(systemName: configuration.isOn ? "checkmark.circle.fill" : "circle")
                    .font(.title3)
                    .foregroundStyle(configuration.isOn ? Color.accentColor : .secondary)
                configuration.label
            }
        }
        .buttonStyle(.plain)
    }
}

extension ToggleStyle where Self == CheckboxToggleStyle {
    static var checkbox: CheckboxToggleStyle { CheckboxToggleStyle() }
}

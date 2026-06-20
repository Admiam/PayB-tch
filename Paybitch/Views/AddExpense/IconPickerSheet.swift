//
//  IconPickerSheet.swift
//  Paybitch
//
//  Grid picker for an expense category icon. `nil` selection = auto-derive from title.
//

import SwiftUI

struct IconPickerSheet: View {
    @Environment(\.dismiss) private var dismiss

    @Binding var selection: String?
    let titleHint: String

    private let columns = Array(repeating: GridItem(.flexible(), spacing: 12), count: 5)
    private var autoSymbol: String { PaybitchIcon.symbol(forTitle: titleHint) }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 22) {
                    autoRow
                    ForEach(PaybitchIcon.categories, id: \.label) { cat in
                        category(cat)
                    }
                }
                .padding(.horizontal, 16)
                .padding(.bottom, 40)
            }
            .background(Paybitch.bg.ignoresSafeArea())
            .navigationTitle("Pick icon")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    PaybitchCloseButton { dismiss() }
                }
            }
        }
    }

    private var autoRow: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("AUTO")
                .font(.spaceGrotesk(11, weight: .bold))
                .tracking(0.6)
                .foregroundStyle(Paybitch.textMuted)
            Button {
                selection = nil
                dismiss()
            } label: {
                HStack(spacing: 12) {
                    iconTile(symbol: autoSymbol, selected: selection == nil)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("From title")
                            .font(.spaceGrotesk(15, weight: .bold))
                            .foregroundStyle(Paybitch.textPrimary)
                        Text("Auto-pick based on what you type")
                            .font(.spaceGrotesk(12))
                            .foregroundStyle(Paybitch.textMuted)
                    }
                    Spacer()
                    if selection == nil {
                        Image(systemName: "checkmark.circle.fill")
                            .foregroundStyle(Paybitch.pink)
                    }
                }
                .padding(12)
                .background(
                    RoundedRectangle(cornerRadius: 18, style: .continuous)
                        .fill(Paybitch.card)
                )
            }
            .buttonStyle(.plain)
        }
    }

    private func category(_ cat: PaybitchIcon.Category) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(cat.label.uppercased())
                .font(.spaceGrotesk(11, weight: .bold))
                .tracking(0.6)
                .foregroundStyle(Paybitch.textMuted)
            LazyVGrid(columns: columns, spacing: 12) {
                ForEach(cat.symbols, id: \.self) { symbol in
                    Button {
                        selection = symbol
                        dismiss()
                    } label: {
                        iconTile(symbol: symbol, selected: selection == symbol)
                    }
                    .buttonStyle(.plain)
                }
            }
        }
    }

    private func iconTile(symbol: String, selected: Bool) -> some View {
        Image(systemName: symbol)
            .font(.system(size: 22, weight: .semibold))
            .foregroundStyle(selected ? .white : Paybitch.textPrimary)
            .frame(width: 54, height: 54)
            .background(
                RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .fill(selected ? Paybitch.pink : Paybitch.card)
            )
            .overlay(
                RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .strokeBorder(
                        selected ? Color.clear : Paybitch.divider,
                        lineWidth: 1
                    )
            )
    }
}

//
//  PaybitchMenu.swift
//  Paybitch
//
//  SwiftUI-style popover menu (blur + label rows + pink trailing icon).
//  Uses native `Menu` for system behaviour, but the menu items are
//  paybitch-themed with our icon palette.
//

import SwiftUI

struct PaybitchMenuButton: View {
    @Environment(ModelData.self) private var model
    let hasSelectedGroup: Bool
    let onSettings: () -> Void
    let onAddGroup: () -> Void
    let onEditGroup: () -> Void
    let onSearch: () -> Void

    var body: some View {
        Menu {
            if hasSelectedGroup {
                Button { onEditGroup() } label: { Label("Edit group", systemImage: "pencil") }
            }
            Button { onAddGroup() } label: { Label("New group", systemImage: "person.3.sequence") }
            Divider()
            if hasSelectedGroup {
                Button { onSearch() } label: { Label("Search activity", systemImage: "magnifyingglass") }
            }
            Divider()
            Button { onSettings() } label: { Label("Settings", systemImage: "gearshape") }
        } label: {
            Image(systemName: "line.3.horizontal")
                .font(.system(size: 22, weight: .bold))
                .frame(width: 44, height: 44)
                .foregroundStyle(Paybitch.textPrimary)
                .glassEffect(
                    .regular.interactive(),
                    in: RoundedRectangle(cornerRadius: Paybitch.radiusButton, style: .continuous)
                )
        }
        .accessibilityLabel("Menu")
    }
}

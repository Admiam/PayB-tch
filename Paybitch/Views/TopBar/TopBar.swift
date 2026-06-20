//
//  TopBar.swift
//  Paybitch
//
//  Header: paybitch wordmark + pink dot + menu button.
//

import SwiftUI

struct TopBar: View {
    let hasSelectedGroup: Bool
    let onSettings: () -> Void
    let onAddGroup: () -> Void
    let onEditGroup: () -> Void
    let onSearch: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            PaybitchWordmark()
            Spacer()
            PaybitchMenuButton(
                hasSelectedGroup: hasSelectedGroup,
                onSettings: onSettings,
                onAddGroup: onAddGroup,
                onEditGroup: onEditGroup,
                onSearch: onSearch
            )
        }
        .padding(.horizontal, 16)
        .padding(.top, 4)
        .padding(.bottom, 6)
    }
}

//
//  TopBar.swift
//  Paybitch
//
//  Created by Adam Míka on 04.08.2025.
//

import SwiftUI

struct TopBar: View {
    @EnvironmentObject var model: ModelData
    @Binding var selected: Group?
    var onAddGroup: () -> Void

    var body: some View {
        GlassEffectContainer(spacing: Theme.Spacing.md) {
            HStack(spacing: Theme.Spacing.md) {
                GroupDropdownList(selection: $selected)
                GlassIconButton(
                    systemName: "plus",
                    accessibilityLabel: "Add group",
                    action: onAddGroup
                )
            }
        }
        .padding(.horizontal, Theme.Spacing.lg)
    }
}

#Preview {
    @Previewable @State var selected: Group?
    let modelData = ModelData()
    return TopBar(selected: $selected, onAddGroup: {})
        .environmentObject(modelData)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Theme.background)
}

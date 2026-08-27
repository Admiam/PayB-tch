//
//  GroupDropdownList.swift
//  Paybitch
//
//  Created by Adam Míka on 04.08.2025.
//

import SwiftUI

/// Glass pill that opens a system menu to pick the active group.
struct GroupDropdownList: View {
    @EnvironmentObject var model: ModelData
    @Binding var selection: Group?

    var body: some View {
        Menu {
            ForEach(model.groups) { group in
                Button {
                    selection = group
                } label: {
                    if selection == group {
                        Label(group.name, systemImage: "checkmark")
                    } else {
                        Text(group.name)
                    }
                }
            }
        } label: {
            label
        }
        .menuStyle(.automatic)
        .tint(.primary)
    }

    private var label: some View {
        HStack(spacing: Theme.Spacing.md) {
            if let group = selection {
                GroupItem(group: group)
            } else {
                VStack(alignment: .leading, spacing: Theme.Spacing.xs) {
                    Text("Select a group")
                        .font(.headline)
                        .foregroundStyle(.primary)
                    Text("Tap to choose")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }

            Spacer(minLength: Theme.Spacing.sm)

            Image(systemName: "chevron.up.chevron.down")
                .font(.footnote.weight(.semibold))
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, Theme.Spacing.lg)
        .frame(maxWidth: .infinity, minHeight: Theme.Size.bar, alignment: .leading)
        .contentShape(
            RoundedRectangle(cornerRadius: Theme.Radius.md, style: .continuous)
        )
        .glassControl(cornerRadius: Theme.Radius.md)
    }
}

#Preview {
    @Previewable @State var selection: Group?
    let modelData = ModelData()
    return GroupDropdownList(selection: $selection)
        .environmentObject(modelData)
        .padding()
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Theme.background)
}

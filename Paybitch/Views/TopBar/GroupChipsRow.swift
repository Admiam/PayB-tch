//
//  GroupChipsRow.swift
//  Paybitch
//
//  Horizontal scrollable group selector. Replaces the dropdown menu.
//

import SwiftUI

struct GroupChipsRow: View {
    @Environment(ModelData.self) private var model
    @Binding var selectedId: String?
    let onAddGroup: () -> Void

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                ForEach(model.groups) { g in
                    GroupChip(
                        name: g.name,
                        memberCount: g.memberIds.count,
                        isActive: g.id == selectedId,
                        action: { selectedId = g.id }
                    )
                }
                NewGroupChip(action: onAddGroup)
            }
            .padding(.horizontal, 16)
            .padding(.bottom, 4)
        }
    }
}

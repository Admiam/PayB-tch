//
//  GroupItem.swift
//  Paybitch
//
//  Created by Adam Míka on 04.08.2025.
//

import SwiftUI

struct GroupItem: View {
    @EnvironmentObject var model: ModelData
    var group: Group

    var body: some View {
        VStack(alignment: .leading) {
            Text(group.name)
            Text("\(model.expenses(forGroup: group.id).count) expenses")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }
}

#Preview {
    let modelData = ModelData()
    GroupItem(group: modelData.groups[0])
        .environmentObject(modelData)
}

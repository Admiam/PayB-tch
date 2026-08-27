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

    private var expenseCount: Int {
        model.expenses(forGroup: group.id).count
    }

    var body: some View {
        VStack(alignment: .leading, spacing: Theme.Spacing.xs) {
            Text(group.name)
                .font(.headline)
                .foregroundStyle(.primary)
            Text("\(expenseCount) \(expenseCount == 1 ? "expense" : "expenses")")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }
}

#Preview {
    let modelData = ModelData()
    GroupItem(group: modelData.groups[0])
        .environmentObject(modelData)
        .padding()
}

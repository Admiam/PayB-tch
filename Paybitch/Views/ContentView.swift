//
//  ContentView.swift
//  Paybitch
//
//  Created by Adam Míka on 01.08.2025.
//

import SwiftUI

struct ContentView: View {
    @EnvironmentObject var model: ModelData
    @State private var selected: Group?

    var body: some View {
        VStack(alignment: .leading, spacing: Theme.Spacing.lg) {
            Text("Groups")
                .font(.largeTitle.bold())
                .foregroundStyle(.primary)
                .padding(.horizontal, Theme.Spacing.lg)
                .padding(.top, Theme.Spacing.lg)

            TopBar(selected: $selected, onAddGroup: addGroup)

            Spacer()
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Theme.background)
        .task {
            if selected == nil { selected = model.groups.first }
        }
    }

    /// Placeholder hook for the future add-group flow.
    private func addGroup() {
        // TODO: present add-group flow once group mutation is supported.
    }
}

#Preview {
    let modelData = ModelData()
    ContentView()
        .environmentObject(modelData)
}

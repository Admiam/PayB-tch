//
//  TopBar.swift
//  Paybitch
//
//  Created by Adam Míka on 04.08.2025.
//

import SwiftUI

struct TopBar: View {
    @EnvironmentObject var model: ModelData
    @State private var selected: Group?
    @State private var isOpen     = false
    
    var body: some View {
        HStack (spacing: 20) {
            GroupDropdownList()
                .environmentObject(model)
            List {
                AddGroupRow {
                    withAnimation(.snappy) { isOpen.toggle() }
                }
            }
            .frame(width: 80, height: 80)
            .listStyle(.plain)
            .clipShape(RoundedRectangle(cornerRadius: 12))   // ← rounds ALL four corners
        }
        .padding(.horizontal)
    }
}

#Preview {
    TopBar()
}

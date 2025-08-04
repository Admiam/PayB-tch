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
    @State private var isOpen     = false

    
    var body: some View {
        VStack {
            HStack (spacing: 5) {
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
                .padding(.trailing)

                
            }
            
            Spacer()
        }
        .background(Color.secondary.opacity(0.1))
    }
}
#Preview {
    let modelData = ModelData()
    ContentView()
        .environmentObject(modelData)
}

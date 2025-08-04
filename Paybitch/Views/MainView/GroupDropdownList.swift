//
//  GroupDropdownList.swift
//  Paybitch
//
//  Created by Adam Míka on 04.08.2025.
//
import SwiftUI

struct GroupDropdownList: View {
    @EnvironmentObject var model: ModelData
    @State private var isOpen     = false
    @State private var selection  : Group? = nil
    
    private let rowHeight: CGFloat = 80

    private var listHeight: CGFloat {
        let headerRows  = 1                                     // the tappable row
        let optionRows  = isOpen
                           ? max(model.groups.count, 1)         // at least 1 for the
                           : 0                                   // “No groups yet” row
        return CGFloat(headerRows + optionRows) * rowHeight
    }
    
    var body: some View {
        List {
            Button {
                withAnimation(.snappy) { isOpen.toggle() }
            } label: {
                HStack {
                    // show chosen group or placeholder text
                    VStack(alignment: .leading) {
                        if let g = selection {
                            GroupItem(group: g)
                        } else {
                            Text("Select a group")
                                .foregroundStyle(.secondary)
                        }
                    }
                    
                    Spacer()
                    Image(systemName: isOpen ? "chevron.up" : "chevron.down")
                        .foregroundStyle(.secondary)
                }
                .frame(height: 70, alignment: .center)

            }
            .buttonStyle(.plain)
            
            // ── drop-down content ────────────────────────────────────────────
            if isOpen {
                if model.groups.isEmpty {                // placeholder when empty
                    ContentUnavailableView("No groups yet",
                                           systemImage: "person.3")
                    .frame(height: rowHeight)
                    .listRowInsets(.init())
                } else {
                    ForEach(model.groups) { group in
                        Button {
                            selection = group
                            withAnimation(.snappy) {
                                isOpen = false
                            }
                        } label: {
                            GroupItem(group: group)
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
        }
        .frame(height: listHeight)
        .listStyle(.plain)
        .scrollContentBackground(.hidden)
        .clipShape(RoundedRectangle(cornerRadius: 12))
        .padding(.horizontal)
    }
}

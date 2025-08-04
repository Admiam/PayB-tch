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

    
    var body: some View {
        List {
            Menu {
                ForEach(model.groups) { group in
                    Button(group.name){
                        selection = group
                    }
                }

            } label: {
                Button {
                    withAnimation(.snappy) { isOpen.toggle() }
                } label: {
                    HStack {
                        VStack(alignment: .leading) {
                            if let g = selection {
                                GroupItem(group: g)
                            } else {
                                Text("Select a group")
                            }
                        }
                        Spacer()
                        Image(systemName: isOpen ? "chevron.up" : "chevron.down")
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .center)
                }
                .frame(height: rowHeight)
                .buttonStyle(.plain)

            }
            .listRowInsets(.init())
            .foregroundStyle(.primary)
            .padding(.horizontal, 12)

        }
        .listStyle(.plain)
        .frame(height: rowHeight)
        .scrollContentBackground(.hidden)
        .clipShape(RoundedRectangle(cornerRadius: 12))
        
    }
}

//
//  AddGroupRow.swift
//  Paybitch
//
//  Created by Adam Míka on 04.08.2025.
//
import SwiftUI

struct AddGroupRow: View {
    var action: () -> Void

    /// Default plain-style list rows are ~44 pt tall on iOS,
    /// so we pin the frame height to that.
    private let rowHeight: CGFloat = 44

    var body: some View {
        Button(action: action) {
            Image(systemName: "plus")
                .font(.body.weight(.semibold))
                .foregroundStyle(.primary)
                .frame(width: 80, height: 80)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .listRowInsets(.init())
//        .frame(width: 60, height: 60)
    }
}

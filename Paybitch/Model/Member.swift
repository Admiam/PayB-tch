//
//  Member.swift
//  Paybitch
//
//  Created by Adam Míka on 02.08.2025.
//
import Foundation

struct Member: Codable, Identifiable, Hashable, Sendable {
    let id: String
    let name: String
    let imageUrl: String?
    /// Optional SF Symbol chosen as the profile picture (monochrome). When set,
    /// it renders instead of the name initials. `nil` = fall back to initials.
    let iconSymbol: String?

    init(id: String, name: String, imageUrl: String? = nil, iconSymbol: String? = nil) {
        self.id = id
        self.name = name
        self.imageUrl = imageUrl
        self.iconSymbol = iconSymbol
    }
}

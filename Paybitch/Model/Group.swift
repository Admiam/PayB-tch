//
//  Group.swift
//  Paybitch
//
//  Created by Adam Míka on 02.08.2025.
//
import Foundation

struct Group: Codable, Identifiable, Hashable {
    let id: String
    let name: String
    let memberIds: [String]
    let defaultCurrency: String
}

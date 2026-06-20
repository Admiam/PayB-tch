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
}

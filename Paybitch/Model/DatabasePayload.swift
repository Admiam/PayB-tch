//
//  DatabasePayload.swift
//  Paybitch
//
//  Created by Adam Míka on 02.08.2025.
//
import Foundation

struct DatabasePayload: Codable {
    let expenses: [Expense]
    let members:  [Member]
    let groups:   [Group]
}

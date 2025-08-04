//
//  Expense.swift
//  Paybitch
//
//  Created by Adam Míka on 02.08.2025.
//
import Foundation

struct Expense: Codable, Identifiable {
    let id: String
    let groupId: String
    let title: String
    let amount: Double
    let currency: String
    let paidBy: String
    let splitAmong: [String]
    let date: String            // ISO yyyy-MM-dd; switch to Date if you prefer
    let notes: String?
}


//
//  ExpenseRepository.swift
//  Paybitch
//
//  Storage abstraction. Local impl now → swap with REST (.NET + Postgres) later.
//

import Foundation

protocol ExpenseRepository: Sendable {
    func list(groupId: String) async throws -> [Expense]
    func listAll() async throws -> [Expense]
    func create(_ expense: Expense) async throws -> Expense
    func update(_ expense: Expense) async throws -> Expense
    func delete(id: String) async throws
}

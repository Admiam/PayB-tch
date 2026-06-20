//
//  GroupRepository.swift
//  Paybitch
//

import Foundation

protocol GroupRepository: Sendable {
    func list() async throws -> [Group]
    func create(_ group: Group) async throws -> Group
    func update(_ group: Group) async throws -> Group
    func delete(id: String) async throws
}

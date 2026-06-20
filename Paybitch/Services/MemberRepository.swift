//
//  MemberRepository.swift
//  Paybitch
//

import Foundation

protocol MemberRepository: Sendable {
    func list() async throws -> [Member]
    func create(_ member: Member) async throws -> Member
    func update(_ member: Member) async throws -> Member
    func delete(id: String) async throws
}

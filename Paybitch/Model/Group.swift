//
//  Group.swift
//  Paybitch
//
import Foundation

struct Group: Codable, Identifiable, Hashable, Sendable {
    let id: String
    var name: String
    var memberIds: [String]
    var defaultCurrency: String

    init(
        id: String = UUID().uuidString,
        name: String,
        memberIds: [String] = [],
        defaultCurrency: String = Currency.default.rawValue
    ) {
        self.id = id
        self.name = name
        self.memberIds = memberIds
        self.defaultCurrency = defaultCurrency
    }
}

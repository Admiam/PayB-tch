//
//  Expense.swift
//  Paybitch
//
import Foundation

struct Expense: Codable, Identifiable, Hashable, Sendable {
    let id: String
    let groupId: String
    var title: String
    var amount: Decimal
    var currency: Currency
    var paidBy: String
    var splitAmong: [String]
    var splitType: SplitType
    var date: Date
    var notes: String?
    var iconSymbol: String?
    var createdAt: Date

    init(
        id: String = UUID().uuidString,
        groupId: String,
        title: String,
        amount: Decimal,
        currency: Currency = .default,
        paidBy: String,
        splitAmong: [String],
        splitType: SplitType = .equal,
        date: Date = .now,
        notes: String? = nil,
        iconSymbol: String? = nil,
        createdAt: Date = .now
    ) {
        self.id = id
        self.groupId = groupId
        self.title = title
        self.amount = amount
        self.currency = currency
        self.paidBy = paidBy
        self.splitAmong = splitAmong
        self.splitType = splitType
        self.date = date
        self.notes = notes
        self.iconSymbol = iconSymbol
        self.createdAt = createdAt
    }

    /// Tolerant decoder — legacy JSON has no splitType / createdAt / iconSymbol and
    /// stored `date` as `yyyy-MM-dd`, `amount` may have been Double.
    enum CodingKeys: String, CodingKey {
        case id, groupId, title, amount, currency, paidBy, splitAmong, splitType, date, notes, iconSymbol, createdAt
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        self.id = try c.decode(String.self, forKey: .id)
        self.groupId = try c.decode(String.self, forKey: .groupId)
        self.title = try c.decode(String.self, forKey: .title)
        self.amount = try Self.decodeDecimal(c, .amount)
        self.currency = (try? c.decode(Currency.self, forKey: .currency)) ?? .default
        self.paidBy = try c.decode(String.self, forKey: .paidBy)
        self.splitAmong = try c.decode([String].self, forKey: .splitAmong)
        self.splitType = (try? c.decode(SplitType.self, forKey: .splitType)) ?? .equal
        self.date = try Self.decodeDate(c, .date)
        self.notes = try? c.decode(String.self, forKey: .notes)
        self.iconSymbol = try? c.decode(String.self, forKey: .iconSymbol)
        self.createdAt = (try? c.decode(Date.self, forKey: .createdAt)) ?? .now
    }

    /// Date may be ISO string (legacy) or native Date (current).
    private static func decodeDate(_ c: KeyedDecodingContainer<CodingKeys>, _ key: CodingKeys) throws -> Date {
        if let d = try? c.decode(Date.self, forKey: key) { return d }
        if let s = try? c.decode(String.self, forKey: key), let d = PaybitchDate.parseISO(s) {
            return d
        }
        throw DecodingError.dataCorruptedError(
            forKey: key,
            in: c,
            debugDescription: "Expected Date or yyyy-MM-dd string"
        )
    }

    /// Amount may be a Double-encoded JSON number. Decimal handles both.
    private static func decodeDecimal(_ c: KeyedDecodingContainer<CodingKeys>, _ key: CodingKeys) throws -> Decimal {
        if let d = try? c.decode(Decimal.self, forKey: key) { return d }
        if let d = try? c.decode(Double.self, forKey: key) { return Decimal(d) }
        throw DecodingError.dataCorruptedError(
            forKey: key,
            in: c,
            debugDescription: "Expected number for amount"
        )
    }
}

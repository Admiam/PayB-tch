//
//  Currency.swift
//  Paybitch
//

import Foundation

enum Currency: String, Codable, Sendable, CaseIterable, Identifiable, Hashable {
    case czk = "CZK"
    case eur = "EUR"
    case usd = "USD"
    case gbp = "GBP"

    var id: String { rawValue }

    var symbol: String {
        switch self {
        case .czk: "Kč"
        case .eur: "€"
        case .usd: "$"
        case .gbp: "£"
        }
    }

    var label: String { "\(rawValue) \(symbol)" }

    /// Decimals shown when value is small enough to render in full.
    var decimals: Int {
        self == .czk ? 0 : 2
    }

    static let `default`: Currency = .czk

    /// Tolerant decoder — supports legacy JSON with arbitrary strings.
    init(from decoder: Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self).uppercased()
        self = Currency(rawValue: raw) ?? .default
    }
}

// MARK: - Cached number formatters per locale + decimals

/// `NSCache` is documented thread-safe for concurrent get/set.
nonisolated(unsafe) private let formatterCache = NSCache<NSString, NumberFormatter>()

private func numberFormatter(decimals: Int, locale: Locale = .current) -> NumberFormatter {
    let key = "\(locale.identifier)|\(decimals)" as NSString
    if let cached = formatterCache.object(forKey: key) { return cached }
    let f = NumberFormatter()
    f.locale = locale
    f.numberStyle = .decimal
    f.minimumFractionDigits = decimals
    f.maximumFractionDigits = decimals
    f.usesGroupingSeparator = true
    formatterCache.setObject(f, forKey: key)
    return f
}

private func numberString(_ decimal: Decimal, decimals: Int) -> String {
    numberFormatter(decimals: decimals).string(from: decimal as NSDecimalNumber) ?? "\(decimal)"
}

// MARK: - Decimal formatting (canonical)

extension Decimal {
    /// Compact display: large numbers abbreviated as `300k Kč` / `1.2M €`,
    /// small numbers respect currency decimals (CZK = 0, others = 2).
    func formatted(in currency: Currency) -> String {
        "\(amountString(in: currency)) \(currency.symbol)"
    }

    /// Compact display, number only (no currency symbol). Useful when the symbol
    /// is rendered separately for typographic hierarchy (e.g. tiles, hero amounts).
    func amountString(in currency: Currency) -> String {
        let absVal = self.magnitude
        if absVal >= 1_000_000 {
            return "\(numberString(self / 1_000_000, decimals: 1))M"
        }
        if absVal >= 10_000 {
            return "\(numberString(self / 1_000, decimals: 0))k"
        }
        return numberString(self, decimals: currency.decimals)
    }

    /// Full precision display, no abbreviation. Used in editors and detail views.
    func formattedFull(in currency: Currency) -> String {
        "\(numberString(self, decimals: currency.decimals)) \(currency.symbol)"
    }

    /// Bankers-rounded to the given scale.
    func rounded(scale: Int, mode: NSDecimalNumber.RoundingMode = .bankers) -> Decimal {
        var input = self
        var output = Decimal()
        NSDecimalRound(&output, &input, scale, mode)
        return output
    }

    var magnitude: Decimal { self < 0 ? -self : self }
}

// MARK: - Double bridge (parsing user input, FX rates)

extension Decimal {
    /// Parses a localized or simple "."/"," number string.
    static func parseUserInput(_ s: String) -> Decimal? {
        let normalized = s.replacingOccurrences(of: ",", with: ".")
        return Decimal(string: normalized, locale: Locale(identifier: "en_US_POSIX"))
    }

    var doubleValue: Double { (self as NSDecimalNumber).doubleValue }
}

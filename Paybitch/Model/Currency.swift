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

    /// Number of decimal places in this currency's minor unit — and therefore both the display
    /// precision *and* the scale all money arithmetic rounds to. The two are the same thing on
    /// purpose: CZK genuinely has no sub-unit any more (haléře were withdrawn), so a CZK amount
    /// with decimals is not a value we round for display, it is a value that cannot exist. This
    /// matches the server, where `Currency.MinorUnits` is the single source of scale.
    var decimals: Int {
        self == .czk ? 0 : 2
    }

    /// Half of one minor unit — the threshold below which a balance is "settled".
    ///
    /// A fixed `0.01` is wrong for CZK, whose smallest unit is a whole koruna: a 0.4 Kč residue
    /// would be treated as a real debt and produce a phantom settle-up edge. Because every amount
    /// is rounded to `decimals`, any genuine balance is at least one minor unit, so half a unit
    /// separates "rounding dust" from "money" for every currency.
    var epsilon: Decimal {
        pow(10, -decimals) / 2
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
    /// Parses a number the way a person types it, in any of the locales this app ships in.
    ///
    /// The naive "swap comma for dot" version silently truncated anything with a thousands
    /// separator — `"1 234,56"` became `1`, and `"1,234.56"` became `1.234` — which is the worst
    /// possible failure mode for an amount field, because it produces a plausible smaller number
    /// instead of an error.
    static func parseUserInput(_ s: String) -> Decimal? {
        // Grouping may be a plain space, NBSP, narrow NBSP (fr/cs) or an apostrophe (de-CH).
        var text = s.filter { !$0.isWhitespace && $0 != "'" && $0 != "\u{2019}" }
        guard !text.isEmpty else { return nil }

        var sign = ""
        if text.hasPrefix("-") || text.hasPrefix("+") {
            sign = text.hasPrefix("-") ? "-" : ""
            text.removeFirst()
        }

        let lastComma = text.lastIndex(of: ",")
        let lastDot = text.lastIndex(of: ".")

        let decimalSeparator: Character?
        switch (lastComma, lastDot) {
        case (nil, nil):
            decimalSeparator = nil
        case (let c?, let d?):
            // Both present: the rightmost is the decimal mark, the other is grouping.
            decimalSeparator = c > d ? "," : "."
        case (let c?, nil):
            decimalSeparator = Self.isGrouping(",", at: c, in: text) ? nil : ","
        case (nil, let d?):
            decimalSeparator = Self.isGrouping(".", at: d, in: text) ? nil : "."
        }

        var digits = ""
        for ch in text {
            if ch.isNumber { digits.append(ch) }
            else if ch == decimalSeparator { digits.append(".") }
            // Any other separator is grouping — drop it.
        }
        guard digits.contains(where: \.isNumber) else { return nil }

        return Decimal(string: sign + digits, locale: Locale(identifier: "en_US_POSIX"))
    }

    /// A single separator is ambiguous (`"1,234"` is 1234 in en-US but 1.234 in cs). Trust the
    /// user's locale first; fall back to the near-universal "grouping runs in threes" convention.
    private static func isGrouping(_ separator: Character, at index: String.Index, in text: String) -> Bool {
        let single = text.filter { $0 == separator }.count == 1
        guard single else { return true } // repeated ⇒ certainly grouping

        let trailingDigits = text.distance(from: text.index(after: index), to: text.endIndex)
        let locale = Locale.current
        if String(separator) == locale.decimalSeparator { return false }
        if String(separator) == locale.groupingSeparator { return trailingDigits == 3 }
        return trailingDigits == 3
    }

    var doubleValue: Double { (self as NSDecimalNumber).doubleValue }
}

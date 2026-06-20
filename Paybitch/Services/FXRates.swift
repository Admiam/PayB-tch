//
//  FXRates.swift
//  Paybitch
//
//  Hardcoded FX provider. Swap with remote (ČNB or backend) by impl `FXProvider`.
//

import Foundation

protocol FXProvider: Sendable {
    func rate(from: Currency, to: Currency) -> Decimal
}

extension FXProvider {
    func convert(_ amount: Decimal, from: Currency, to: Currency) -> Decimal {
        amount * rate(from: from, to: to)
    }
}

struct StaticFXProvider: FXProvider {
    /// Rates expressed in CZK per 1 unit of currency.
    static let toCZK: [Currency: Decimal] = [
        .czk: 1.00,
        .eur: 25.20,
        .usd: 23.10,
        .gbp: 29.40,
    ]

    func rate(from: Currency, to: Currency) -> Decimal {
        let f = Self.toCZK[from] ?? 1
        let t = Self.toCZK[to] ?? 1
        return f / t
    }
}

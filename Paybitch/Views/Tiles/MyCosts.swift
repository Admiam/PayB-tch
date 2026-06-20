//
//  MyCosts.swift
//  Paybitch
//

import SwiftUI

struct MyCosts: View {
    let value: Decimal
    let currency: Currency
    /// 0...1 — share of total group costs.
    let ratio: Double

    private var sub: String {
        "\(Int((ratio * 100).rounded()))% of total"
    }

    var body: some View {
        PaybitchTile(
            label: "My costs",
            amount: value.amountString(in: currency),
            currencySymbol: currency.symbol,
            sub: sub,
            ratio: ratio,
            background: Paybitch.pink,
            foreground: .white,
            icon: { Image(systemName: "wallet.bifold.fill").font(.system(size: 18, weight: .bold)) }
        )
    }
}

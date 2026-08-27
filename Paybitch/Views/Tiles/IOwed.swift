//
//  IOwed.swift
//  Paybitch
//
//  Big tile — "I am owed" / "I owe" with bar viz.
//

import SwiftUI

struct IOwed: View {
    let net: Decimal
    let currency: Currency
    /// Sub label, e.g. `"from 3 people"` / `"to 2 people"`.
    let sub: String?
    /// 0...1 — fraction of the group total this represents.
    let ratio: Double

    private var isOwedToMe: Bool { net >= 0 }
    private var label: String { isOwedToMe ? "I am owed" : "I owe" }
    private var icon: String { isOwedToMe ? "arrow.down" : "arrow.up" }

    var body: some View {
        PaybitchTile(
            label: label,
            amount: net.magnitude.amountString(in: currency),
            currencySymbol: currency.symbol,
            sub: sub,
            ratio: ratio,
            amountColor: Paybitch.pink,
            isBig: true,
            icon: { Image(systemName: icon).font(.system(size: 20, weight: .bold)) }
        )
    }
}

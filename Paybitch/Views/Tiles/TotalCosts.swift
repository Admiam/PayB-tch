//
//  TotalCosts.swift
//  Paybitch
//

import SwiftUI

struct TotalCosts: View {
    let value: Decimal
    let currency: Currency
    let expenseCount: Int

    private var sub: String {
        "\(expenseCount) \(expenseCount == 1 ? "expense" : "expenses")"
    }

    var body: some View {
        PaybitchTile(
            label: "Total",
            amount: value.amountString(in: currency),
            currencySymbol: currency.symbol,
            sub: sub,
            ratio: 0,
            amountColor: Paybitch.textPrimary,
            icon: { Image(systemName: "doc.text.fill").font(.system(size: 18, weight: .bold)) }
        )
    }
}

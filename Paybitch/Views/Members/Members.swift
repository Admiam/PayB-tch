//
//  Members.swift
//  Paybitch
//

import SwiftUI

struct Members: View {
    @Environment(ModelData.self) private var model
    let group: Group

    private var displayCurrency: Currency {
        Currency(rawValue: group.defaultCurrency.uppercased()) ?? .default
    }

    private var balances: [MemberBalance] {
        BalanceCalculator.balances(
            expenses: model.expenses(forGroup: group.id),
            memberIds: group.memberIds,
            in: displayCurrency,
            fx: model.fx
        )
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            PaybitchSectionHeader(title: "The Crew", badge: "\(group.memberIds.count)")
                .padding(.horizontal, -8)
            MemberList(balances: balances, currency: displayCurrency)
                .background(
                    RoundedRectangle(cornerRadius: Paybitch.radiusCard, style: .continuous)
                        .fill(Paybitch.card)
                )
        }
    }
}

let avatarColors: [Color] = [
    Color(red: 0.235, green: 0.773, blue: 1.0),    // #3CC5FF
    Color(red: 1.0, green: 0.314, blue: 0.592),    // #FF5097
    Color(red: 0.780, green: 0.357, blue: 1.0),    // #C75BFF
    Color(red: 0.882, green: 0.345, blue: 1.0),    // #E158FF
    Color(red: 1.0, green: 0.694, blue: 0.239),    // #FFB13D
    Color(red: 0.298, green: 0.776, blue: 0.741),  // #4CC6BD teal (was lime)
    Color(red: 1.0, green: 0.541, blue: 0.239),    // #FF8A3D
]

extension Color {
    static func palette(for string: String) -> Color {
        let index = abs(string.hashValue) % avatarColors.count
        return avatarColors[index]
    }
}

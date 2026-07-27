//
//  MemberList.swift
//  Paybitch
//
//  Horizontal strip of crew avatars with each member's net balance.
//

import SwiftUI

struct MemberList: View {
    @Environment(ModelData.self) private var model
    let balances: [MemberBalance]
    let currency: Currency

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 6) {
                ForEach(balances) { bal in
                    if let m = model.member(for: bal.memberId) {
                        MemberCrewChip(
                            member: m,
                            net: bal.net,
                            currency: currency,
                            isMe: model.currentUserId == m.id
                        )
                    }
                }
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 14)
        }
    }
}

private struct MemberCrewChip: View {
    let member: Member
    let net: Decimal
    let currency: Currency
    let isMe: Bool

    private var color: Color {
        if net > currency.epsilon { return Paybitch.positive }
        if net < -currency.epsilon { return Paybitch.negative }
        return Paybitch.textMuted
    }

    private var prefix: String {
        if net > currency.epsilon { return "+" }
        if net < -currency.epsilon { return "−" }
        return ""
    }

    var body: some View {
        VStack(spacing: 6) {
            PaybitchAvatar(member: member, size: 48, isMe: isMe)
            Text(isMe ? "Me" : member.name)
                .font(.spaceGrotesk(13, weight: .bold))
                .foregroundStyle(Paybitch.textPrimary)
                .lineLimit(1)
            Text("\(prefix)\(net.magnitude.amountString(in: currency)) \(currency.symbol)")
                .font(.spaceGrotesk(11, weight: .bold))
                .foregroundStyle(color)
                .lineLimit(1)
        }
        .frame(minWidth: 64)
    }
}

//
//  PaybitchAvatar.swift
//  Paybitch
//
//  Replaces MemberIcon with the redesigned avatar (initials, color palette,
//  optional ring + ME badge). Existing MemberIcon stays for legacy callers.
//

import SwiftUI

struct PaybitchAvatar: View {
    let member: Member
    var size: CGFloat = 48
    var isMe: Bool = false
    var showRing: Bool = false

    var body: some View {
        ZStack(alignment: .topTrailing) {
            base
                .overlay(
                    Circle()
                        .stroke(Paybitch.pink, lineWidth: 2)
                        .padding(-3)
                        .opacity(showRing ? 1 : 0)
                )
            if isMe {
                Text("ME")
                    .font(.spaceGrotesk(8, weight: .heavy))
                    .tracking(0.5)
                    .foregroundStyle(.white)
                    .padding(.horizontal, 5)
                    .padding(.vertical, 2)
                    .background(
                        RoundedRectangle(cornerRadius: 6, style: .continuous).fill(Paybitch.pink)
                    )
                    .overlay(
                        RoundedRectangle(cornerRadius: 6, style: .continuous)
                            .stroke(Paybitch.bg, lineWidth: 2)
                    )
                    .offset(x: 4, y: -4)
            }
        }
        .frame(width: size, height: size)
    }

    @ViewBuilder
    private var base: some View {
        if let urlString = member.imageUrl, let url = URL(string: urlString) {
            AsyncImage(url: url) { phase in
                switch phase {
                case .success(let img):
                    img.resizable().scaledToFill()
                case .empty:
                    Circle().fill(Color.palette(for: member.id))
                default:
                    initialsFallback
                }
            }
            .frame(width: size, height: size)
            .clipShape(Circle())
        } else {
            initialsFallback
        }
    }

    private var initialsFallback: some View {
        Text(initials)
            .font(.spaceGrotesk(size * 0.36, weight: .heavy))
            .tracking(0.5)
            .foregroundStyle(.white)
            .frame(width: size, height: size)
            .background(Circle().fill(Color.palette(for: member.id)))
    }

    private var initials: String {
        let parts = member.name.split(separator: " ")
        let chars = parts.compactMap { $0.first }.prefix(2)
        let s = String(chars).uppercased()
        return s.isEmpty ? "?" : s
    }
}

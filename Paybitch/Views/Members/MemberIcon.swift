//
//  MemberIcon.swift
//  Paybitch
//
//  Created by Adam Míka on 15.09.2025.
//

import SwiftUI

struct MemberIcon: View {
    let member: Member
    let size: CGFloat

    var body: some View {
        SwiftUI.Group {
            if let urlString = member.imageUrl, let url = URL(string: urlString) {
                AsyncImage(url: url) { phase in
                    switch phase {
                    case .failure:
                        fallback
                    case .success(let image):
                        image
                            .resizable()
                            .scaledToFill()
                    case .empty:
                        ProgressView()
                    @unknown default:
                        fallback
                    }
                }
            } else {
                fallback
            }
        }
        .frame(width: size, height: size)
        .clipShape(Circle())
        .overlay(
            Circle().strokeBorder(.white.opacity(0.6), lineWidth: 1)
        )
    }

    private var fallback: some View {
        Text(member.name.prefix(2).uppercased())
            .font(.headline)
            .foregroundStyle(.white)
            .frame(width: size, height: size)
            .background(Circle().fill(Color.palette(for: member.id)))
    }
}

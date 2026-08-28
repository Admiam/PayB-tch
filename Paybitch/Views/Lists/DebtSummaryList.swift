//
//  DebtSummaryList.swift
//  Paybitch
//
//  "Who owes whom" — circular debt-flow viz + per-edge rows.
//

import SwiftUI

struct DebtSummaryList: View {
    /// Derived once by the dashboard — see `GroupSummary`.
    let summary: GroupSummary

    private var edges: [DebtEdge] { summary.edges }

    var body: some View {
        if !edges.isEmpty {
            VStack(alignment: .leading, spacing: 0) {
                PaybitchSectionHeader(title: "Who owes whom", badge: "\(edges.count)")
                    .padding(.horizontal, -8)

                VStack(spacing: 0) {
                    DebtFlowCanvas(edges: edges)
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 8)
                    Divider().background(Paybitch.divider)
                    VStack(spacing: 0) {
                        ForEach(edges) { edge in
                            DebtRow(edge: edge, currency: summary.currency)
                        }
                    }
                    .padding(.top, 8)
                }
                .padding(8)
                .background(
                    RoundedRectangle(cornerRadius: Paybitch.radiusCard, style: .continuous)
                        .fill(Paybitch.card)
                )
            }
        }
    }
}

private struct DebtRow: View {
    @Environment(ModelData.self) private var model
    let edge: DebtEdge
    let currency: Currency

    var body: some View {
        HStack(spacing: 10) {
            if let from = model.member(for: edge.from) {
                PaybitchAvatar(member: from, size: 32, isMe: edge.from == model.currentUserId)
            }
            Image(systemName: "arrow.right")
                .font(.system(size: 11, weight: .bold))
                .foregroundStyle(Paybitch.pink)
            if let to = model.member(for: edge.to) {
                PaybitchAvatar(member: to, size: 32, isMe: edge.to == model.currentUserId)
            }
            HStack(spacing: 4) {
                Text(model.displayName(for: edge.from))
                    .font(.spaceGrotesk(14, weight: .semibold))
                    .foregroundStyle(Paybitch.textPrimary)
                Text("→")
                    .foregroundStyle(Paybitch.textPrimary.opacity(0.4))
                Text(model.displayName(for: edge.to))
                    .font(.spaceGrotesk(14, weight: .semibold))
                    .foregroundStyle(Paybitch.textPrimary)
            }
            .lineLimit(1)
            Spacer()
            Text(edge.amount.formatted(in: currency))
                .font(.spaceGrotesk(15, weight: .heavy))
                .foregroundStyle(Paybitch.textPrimary)
                .monospacedDigit()
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
    }
}

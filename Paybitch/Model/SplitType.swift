//
//  SplitType.swift
//  Paybitch
//

import Foundation

enum SplitType: Codable, Sendable, Hashable {
    /// Even split among `splitAmong` members.
    case equal
    /// Exact amounts per member id (must sum to expense amount).
    case exact([String: Decimal])
    /// Integer share weights per member id.
    case shares([String: Int])

    enum Kind: String, Codable, Sendable, CaseIterable, Identifiable {
        case equal, exact, shares
        var id: String { rawValue }
        var label: String {
            switch self {
            case .equal: "Equal"
            case .exact: "Exact"
            case .shares: "Shares"
            }
        }
    }

    var kind: Kind {
        switch self {
        case .equal: .equal
        case .exact: .exact
        case .shares: .shares
        }
    }

    /// Compute per-member share of `total`. The sum of returned values
    /// equals `total` exactly — any rounding remainder is given to the
    /// last member to avoid silent cent loss across many expenses.
    func shares(
        total: Decimal,
        among members: [String],
        roundedTo scale: Int = 2
    ) -> [String: Decimal] {
        guard !members.isEmpty else { return [:] }
        switch self {
        case .equal:
            return distributeEqually(total: total, members: members, scale: scale)
        case .exact(let map):
            return members.reduce(into: [:]) { $0[$1] = map[$1] ?? 0 }
        case .shares(let weights):
            let totalWeight = weights.values.reduce(0, +)
            guard totalWeight > 0 else { return [:] }
            return distributeByWeights(
                total: total,
                members: members,
                weights: weights,
                totalWeight: totalWeight,
                scale: scale
            )
        }
    }

    // MARK: - Helpers

    private func distributeEqually(
        total: Decimal,
        members: [String],
        scale: Int
    ) -> [String: Decimal] {
        let count = Decimal(members.count)
        let each = (total / count).rounded(scale: scale)
        var result: [String: Decimal] = [:]
        var assigned: Decimal = 0
        for (i, m) in members.enumerated() {
            if i == members.count - 1 {
                result[m] = total - assigned
            } else {
                result[m] = each
                assigned += each
            }
        }
        return result
    }

    private func distributeByWeights(
        total: Decimal,
        members: [String],
        weights: [String: Int],
        totalWeight: Int,
        scale: Int
    ) -> [String: Decimal] {
        var result: [String: Decimal] = [:]
        var assigned: Decimal = 0
        let totalW = Decimal(totalWeight)
        for (i, m) in members.enumerated() {
            let w = Decimal(weights[m] ?? 0)
            if i == members.count - 1 {
                result[m] = total - assigned
            } else {
                let share = (total * w / totalW).rounded(scale: scale)
                result[m] = share
                assigned += share
            }
        }
        return result
    }
}

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

    /// Per-member share of `total`, in minor units of the given scale.
    ///
    /// **Postcondition:** for a non-empty `members`, the returned values sum to `total` exactly.
    /// Nothing is ever created or lost — that invariant is what keeps `Σ net == 0` per currency.
    ///
    /// Allocation is largest-remainder (Hamilton), byte-identical to the server's
    /// `Paybitch.Domain.Splitting.Splitter.ProportionalAllocate`: distribute the floor of each
    /// member's proportional claim, then hand the leftover minor units out one each to the largest
    /// remainders, breaking ties by descending weight and then by ordinal member id. The remainder
    /// is spread fairly rather than dumped on whoever happens to be last in the array — which was
    /// both unfair and, because `splitAmong` is built from a `Set`, non-deterministic between runs.
    func shares(
        total: Decimal,
        among members: [String],
        roundedTo scale: Int = 2
    ) -> [String: Decimal] {
        let ids = Self.distinct(members)
        guard !ids.isEmpty else { return [:] }

        let negative = total < 0
        let totalMinor = Self.toMinor(total.magnitude, scale: scale)

        let allocated: [Int]
        switch self {
        case .equal:
            allocated = Self.allocate(totalMinor: totalMinor, ids: ids, weights: ids.map { _ in 1 })

        case .shares(let weights):
            // Negative weights are meaningless; clamp so a bad payload cannot invert a debt.
            let w = ids.map { max(0, weights[$0] ?? 0) }
            allocated = Self.allocate(totalMinor: totalMinor, ids: ids, weights: w)

        case .exact(let map):
            let claims = ids.map { Self.toMinor((map[$0] ?? 0).magnitude, scale: scale) }
            let claimed = claims.reduce(0, +)
            if claimed == totalMinor {
                // The amounts already reconcile — pass them through untouched, exactly as the
                // server does (it validates Σ == total and rejects anything else).
                allocated = claims
            } else {
                // They do not, and the only case that reaches here legitimately is an expense
                // recorded in one currency being reported in another: `BalanceCalculator` converts
                // the total but the stored per-member amounts are still in the original currency.
                // Re-project the same proportions onto the converted total. Without this the payer
                // is credited the converted amount while members are debited the unconverted one,
                // and the group's balances silently stop summing to zero.
                allocated = Self.allocate(totalMinor: totalMinor, ids: ids, weights: claims)
            }
        }

        var result: [String: Decimal] = Dictionary(minimumCapacity: ids.count)
        for (i, id) in ids.enumerated() {
            let value = Self.fromMinor(allocated[i], scale: scale)
            result[id] = negative ? -value : value
        }
        return result
    }

    // MARK: - Allocation

    /// Largest-remainder allocation of `totalMinor` across `ids` by integer `weights`.
    /// Mirrors the server's `Splitter.ProportionalAllocate` including its tie-break.
    ///
    /// A weight vector that sums to zero carries no information about how to divide the money, yet
    /// the money still has to land somewhere for the ledger to balance — so it degrades to an even
    /// split rather than returning nothing. Returning an empty allocation (the previous behaviour)
    /// meant the payer was credited while nobody was debited: money out of thin air.
    private static func allocate(totalMinor: Int, ids: [String], weights: [Int]) -> [Int] {
        let totalWeight = weights.reduce(0, +)
        guard totalWeight > 0 else {
            return allocate(totalMinor: totalMinor, ids: ids, weights: ids.map { _ in 1 })
        }

        let bigTotal = Decimal(totalMinor)
        let bigWeight = Decimal(totalWeight)

        var floors = [Int](repeating: 0, count: ids.count)
        var remainders = [Decimal](repeating: .zero, count: ids.count)
        var assigned = 0

        for i in ids.indices {
            // Decimal carries 38 significant digits, so `totalMinor * weight` cannot overflow the
            // way an Int64 product could. This is the stand-in for the server's Int128.
            let numerator = bigTotal * Decimal(weights[i])
            let quotient = (numerator / bigWeight).rounded(scale: 0, mode: .down)
            let floor = toInt(quotient)
            floors[i] = floor
            remainders[i] = numerator - quotient * bigWeight
            assigned += floor
        }

        // 0 <= leftover < ids.count
        let leftover = totalMinor - assigned
        guard leftover > 0 else { return floors }

        let order = ids.indices.sorted { a, b in
            if remainders[a] != remainders[b] { return remainders[a] > remainders[b] }
            if weights[a] != weights[b] { return weights[a] > weights[b] }
            // Ordinal comparison — `String.<` uses Unicode canonical ordering, which would diverge
            // from the server's StringComparer.Ordinal on non-ASCII ids.
            return ids[a].utf16.lexicographicallyPrecedes(ids[b].utf16)
        }
        for k in 0..<Swift.min(leftover, order.count) {
            floors[order[k]] += 1
        }
        return floors
    }

    // MARK: - Minor units

    /// Members, de-duplicated, in first-seen order. The server rejects duplicate ids outright; the
    /// client is more forgiving because `splitAmong` is user-editable, but a duplicate must not be
    /// allowed to claim a second share.
    private static func distinct(_ members: [String]) -> [String] {
        var seen = Set<String>(minimumCapacity: members.count)
        return members.filter { seen.insert($0).inserted }
    }

    private static func toMinor(_ value: Decimal, scale: Int) -> Int {
        toInt((value * pow(10, scale)).rounded(scale: 0))
    }

    private static func fromMinor(_ minor: Int, scale: Int) -> Decimal {
        Decimal(minor) / pow(10, scale)
    }

    private static func toInt(_ value: Decimal) -> Int {
        NSDecimalNumber(decimal: value).intValue
    }
}

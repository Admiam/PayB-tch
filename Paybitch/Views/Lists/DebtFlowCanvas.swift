//
//  DebtFlowCanvas.swift
//  Paybitch
//
//  Circular debt-flow visualization. Members arranged on a circle; debts
//  drawn as curved animated arrows with stroke width scaled by amount.
//

import SwiftUI

struct DebtFlowCanvas: View {
    @Environment(ModelData.self) private var model
    let edges: [DebtEdge]
    var size: CGFloat = 320

    /// Unique participant ids in stable order (insertion order across edges).
    private var participantIds: [String] {
        var seen: Set<String> = []
        var ids: [String] = []
        for e in edges {
            if seen.insert(e.from).inserted { ids.append(e.from) }
            if seen.insert(e.to).inserted { ids.append(e.to) }
        }
        return ids
    }

    private func position(index: Int, count: Int, in size: CGFloat) -> CGPoint {
        let cx = size / 2
        let cy = size / 2
        let r = size * 0.36
        let angle = -CGFloat.pi / 2 + (CGFloat(index) / CGFloat(count)) * .pi * 2
        return CGPoint(x: cx + cos(angle) * r, y: cy + sin(angle) * r)
    }

    var body: some View {
        let ids = participantIds
        let positions: [String: CGPoint] = Dictionary(
            uniqueKeysWithValues: ids.enumerated().map { ($0.element, position(index: $0.offset, count: ids.count, in: size)) }
        )
        let maxAmount = max((edges.map { $0.amount.doubleValue }.max() ?? 1), 1)

        return ZStack {
            TimelineView(.animation(minimumInterval: 1.0 / 30.0)) { context in
                Canvas { ctx, _ in
                    let phase = context.date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: 1.5) / 1.5
                    let dashOffset = -CGFloat(phase) * 24

                    for edge in edges {
                        guard let a = positions[edge.from], let b = positions[edge.to] else { continue }
                        drawArrow(ctx: ctx, from: a, to: b, center: CGPoint(x: size/2, y: size/2),
                                  amount: edge.amount.doubleValue, maxAmount: maxAmount,
                                  dashOffset: dashOffset)
                    }
                }
            }
            ForEach(ids, id: \.self) { id in
                if let m = model.member(for: id), let p = positions[id] {
                    VStack(spacing: 4) {
                        PaybitchAvatar(member: m, size: 48, isMe: id == model.currentUserId)
                        Text(model.displayName(for: id))
                            .font(.spaceGrotesk(10, weight: .bold))
                            .foregroundStyle(Paybitch.textPrimary)
                            .lineLimit(1)
                    }
                    .frame(width: 80)
                    .position(x: p.x, y: p.y + 4)
                }
            }
        }
        .frame(width: size, height: size)
    }

    private func drawArrow(
        ctx: GraphicsContext,
        from a: CGPoint,
        to b: CGPoint,
        center: CGPoint,
        amount: Double,
        maxAmount: Double,
        dashOffset: CGFloat
    ) {
        // Curve outwards from center: control point pushed away.
        let mid = CGPoint(x: (a.x + b.x) / 2, y: (a.y + b.y) / 2)
        let dx = mid.x - center.x
        let dy = mid.y - center.y
        let k: CGFloat = 0.35
        let control = CGPoint(x: mid.x + dx * k, y: mid.y + dy * k)

        // Shorten endpoints to clear the avatar.
        let endR: CGFloat = 30
        let startR: CGFloat = 28
        let end = shorten(from: control, to: b, distance: endR)
        let start = shorten(from: control, to: a, distance: startR)

        var path = Path()
        path.move(to: start)
        path.addQuadCurve(to: end, control: control)

        let width = 2 + CGFloat(amount / maxAmount) * 4
        ctx.stroke(
            path,
            with: .color(Paybitch.pink),
            style: StrokeStyle(
                lineWidth: width,
                lineCap: .round,
                lineJoin: .round,
                dash: [6, 6],
                dashPhase: dashOffset
            )
        )

        // Arrow head at end.
        let dirX = end.x - control.x
        let dirY = end.y - control.y
        let len = max(hypot(dirX, dirY), 0.001)
        let ux = dirX / len
        let uy = dirY / len
        let headSize: CGFloat = 7
        let p1 = CGPoint(x: end.x - ux * headSize - uy * headSize * 0.6,
                         y: end.y - uy * headSize + ux * headSize * 0.6)
        let p2 = CGPoint(x: end.x - ux * headSize + uy * headSize * 0.6,
                         y: end.y - uy * headSize - ux * headSize * 0.6)
        var head = Path()
        head.move(to: end)
        head.addLine(to: p1)
        head.addLine(to: p2)
        head.closeSubpath()
        ctx.fill(head, with: .color(Paybitch.pink))
    }

    private func shorten(from origin: CGPoint, to target: CGPoint, distance: CGFloat) -> CGPoint {
        let vx = target.x - origin.x
        let vy = target.y - origin.y
        let len = max(hypot(vx, vy), 0.001)
        return CGPoint(x: target.x - (vx / len) * distance, y: target.y - (vy / len) * distance)
    }
}

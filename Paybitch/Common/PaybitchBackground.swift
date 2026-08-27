//
//  PaybitchBackground.swift
//  Paybitch
//
//  Neutral bg: dark/light base, a single faint pink accent glow (top-right),
//  subtle dot pattern overlay.
//

import SwiftUI

private struct PaybitchBackgroundModifier: ViewModifier {
    @Environment(\.colorScheme) private var scheme

    func body(content: Content) -> some View {
        content.background(
            ZStack {
                Paybitch.bg
                AmbientGlow()
                DotPattern()
            }
            .ignoresSafeArea()
        )
    }
}

private struct AmbientGlow: View {
    @Environment(\.colorScheme) private var scheme

    /// A single, restrained pink accent glow — keeps the canvas neutral.
    private var pinkOpacity: Double { scheme == .dark ? 0.16 : 0.09 }

    var body: some View {
        GeometryReader { geo in
            RadialGradient(
                colors: [Paybitch.pink.opacity(pinkOpacity), .clear],
                center: .topTrailing,
                startRadius: 0,
                endRadius: max(geo.size.width, geo.size.height) * 0.7
            )
            .allowsHitTesting(false)
        }
    }
}

private struct DotPattern: View {
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        Canvas { ctx, size in
            let step: CGFloat = 20
            let r: CGFloat = 1
            let color = scheme == .dark
                ? Color.white.opacity(0.04)
                : Color.black.opacity(0.04)
            var y: CGFloat = 0
            while y < size.height {
                var x: CGFloat = 0
                while x < size.width {
                    let rect = CGRect(x: x, y: y, width: r * 2, height: r * 2)
                    ctx.fill(Path(ellipseIn: rect), with: .color(color))
                    x += step
                }
                y += step
            }
        }
        .allowsHitTesting(false)
    }
}

extension View {
    func paybitchBackground() -> some View {
        modifier(PaybitchBackgroundModifier())
    }
}

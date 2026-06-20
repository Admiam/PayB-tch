//
//  PaybitchBackground.swift
//  Paybitch
//
//  Brand bg: dark/light base, two ambient radial glows (pink top-right,
//  lime bottom-left), subtle dot pattern overlay.
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

    private var pinkOpacity: Double { scheme == .dark ? 0.30 : 0.18 }
    private var limeOpacity: Double { scheme == .dark ? 0.18 : 0.22 }

    var body: some View {
        GeometryReader { geo in
            ZStack {
                RadialGradient(
                    colors: [Paybitch.pink.opacity(pinkOpacity), .clear],
                    center: .topTrailing,
                    startRadius: 0,
                    endRadius: max(geo.size.width, geo.size.height) * 0.7
                )
                RadialGradient(
                    colors: [Paybitch.lime.opacity(limeOpacity), .clear],
                    center: .bottomLeading,
                    startRadius: 0,
                    endRadius: max(geo.size.width, geo.size.height) * 0.7
                )
            }
            .blendMode(.plusLighter)
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

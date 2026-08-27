//
//  PaybitchGlass.swift
//  Paybitch
//
//  Unified Liquid Glass control system (iOS 26). One button style for the
//  whole app so every action shares the same material, shape, and press feel.
//

import SwiftUI

extension Paybitch {
    /// Corner radius for glass action buttons / rows.
    static let radiusButton: CGFloat = 16
    /// Press feedback scale for glass controls.
    static let pressedScale: CGFloat = 0.96
}

/// Visual intent of a glass control.
enum PaybitchButtonKind {
    /// Pink-tinted glass — primary actions.
    case accent
    /// Clear glass — secondary / icon actions.
    case neutral
    /// Red-tinted glass — destructive actions.
    case destructive
}

/// The single Liquid Glass button style used across Paybitch.
struct PaybitchGlassButton<S: Shape>: ButtonStyle {
    var kind: PaybitchButtonKind = .neutral
    var shape: S
    var dimmed: Bool = false

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .glassEffect(glass.interactive(), in: shape)
            .scaleEffect(configuration.isPressed ? Paybitch.pressedScale : 1)
            .opacity(dimmed ? 0.4 : 1)
            .animation(.snappy(duration: 0.22), value: configuration.isPressed)
    }

    private var glass: Glass {
        switch kind {
        case .accent:      return .regular.tint(Paybitch.pink)
        case .neutral:     return .regular
        case .destructive: return .regular.tint(Paybitch.negative)
        }
    }
}

#Preview {
    VStack(spacing: 16) {
        Button("Accent") {}
            .foregroundStyle(.white)
            .padding(.horizontal, 20).padding(.vertical, 12)
            .buttonStyle(PaybitchGlassButton(kind: .accent, shape: Capsule()))
        Button("Neutral") {}
            .foregroundStyle(Paybitch.textPrimary)
            .padding(.horizontal, 20).padding(.vertical, 12)
            .buttonStyle(PaybitchGlassButton(kind: .neutral, shape: Capsule()))
    }
    .padding()
    .frame(maxWidth: .infinity, maxHeight: .infinity)
    .paybitchBackground()
}

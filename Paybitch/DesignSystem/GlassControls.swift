//
//  GlassControls.swift
//  Paybitch
//
//  One unified Liquid Glass treatment for every custom control in the app so
//  buttons share the same material, shape language, and press response.
//

import SwiftUI

/// Shared Liquid Glass surface. Applied to any control's content so the picker,
/// the add button, and anything added later all read as the same material.
struct GlassControl: ViewModifier {
    var cornerRadius: CGFloat = Theme.Radius.md
    var isPressed: Bool = false

    func body(content: Content) -> some View {
        content
            .glassEffect(
                .regular.interactive(),
                in: RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
            )
            .scaleEffect(isPressed ? 0.97 : 1)
            .animation(Theme.Motion.snappy, value: isPressed)
    }
}

extension View {
    /// Applies the app's unified Liquid Glass control surface.
    func glassControl(cornerRadius: CGFloat = Theme.Radius.md, isPressed: Bool = false) -> some View {
        modifier(GlassControl(cornerRadius: cornerRadius, isPressed: isPressed))
    }
}

/// Unified button style for tappable glass controls (icon or text).
struct GlassControlButtonStyle: ButtonStyle {
    var cornerRadius: CGFloat = Theme.Radius.md

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .foregroundStyle(.primary)
            .glassControl(cornerRadius: cornerRadius, isPressed: configuration.isPressed)
    }
}

extension ButtonStyle where Self == GlassControlButtonStyle {
    /// The app's standard Liquid Glass button.
    static var glassControl: GlassControlButtonStyle { .init() }

    static func glassControl(cornerRadius: CGFloat) -> GlassControlButtonStyle {
        .init(cornerRadius: cornerRadius)
    }
}

/// Reusable square icon button rendered on Liquid Glass — the unified control
/// for compact actions like "add".
struct GlassIconButton: View {
    let systemName: String
    let accessibilityLabel: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: systemName)
                .font(.title3.weight(.semibold))
                .frame(width: Theme.Size.control, height: Theme.Size.control)
                .contentShape(
                    RoundedRectangle(cornerRadius: Theme.Radius.md, style: .continuous)
                )
        }
        .buttonStyle(.glassControl)
        .accessibilityLabel(Text(accessibilityLabel))
    }
}

#Preview {
    GlassEffectContainer(spacing: Theme.Spacing.md) {
        HStack(spacing: Theme.Spacing.md) {
            Button("Glass button") {}
                .buttonStyle(.glassControl)
            GlassIconButton(systemName: "plus", accessibilityLabel: "Add") {}
        }
    }
    .padding()
    .frame(maxWidth: .infinity, maxHeight: .infinity)
    .background(Theme.background)
}

//
//  PaybitchBackground.swift
//  Paybitch
//
//  Flat dark/light base. No glow, no texture — both showed through Liquid Glass
//  surfaces (chips, buttons) as a visible warp rather than a clean frosted blur.
//

import SwiftUI

private struct PaybitchBackgroundModifier: ViewModifier {
    func body(content: Content) -> some View {
        content.background(Paybitch.bg.ignoresSafeArea())
    }
}

extension View {
    func paybitchBackground() -> some View {
        modifier(PaybitchBackgroundModifier())
    }
}

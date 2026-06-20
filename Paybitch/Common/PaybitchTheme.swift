//
//  PaybitchTheme.swift
//  Paybitch
//

import SwiftUI

/// Paybitch design tokens — colors, typography, radii, shadows.
enum Paybitch {

    // MARK: Brand colors (asset-backed)
    static let pink = Color.brandPink   // #FF5097
    static let lime = Color.brandLime   // #E4F482

    // MARK: Adaptive surfaces (light/dark)
    static let bg = Color("Bg")
    static let card = Color("Card")
    static let chipBg = Color("ChipBg")
    static let tile3 = Color("Tile3")
    static let textPrimary = Color("TextPrimary")
    static let textMuted = Color("TextMuted")
    static let divider = Color("Divider")
    static let menuBg = Color("MenuBg")

    // MARK: Status
    static let positive = Color(red: 124/255, green: 203/255, blue: 82/255)   // #7CCB52
    static let negative = Color(red: 255/255, green: 85/255, blue: 114/255)   // #FF5572

    // MARK: Radii
    static let radiusTile: CGFloat = 22
    static let radiusCard: CGFloat = 22
    static let radiusRow: CGFloat = 18
    static let radiusSheet: CGFloat = 28
    static let radiusChip: CGFloat = 999

    enum Shadow {
        static let tile = ShadowStyle(color: .black.opacity(0.18), radius: 22, x: 0, y: 8)
        static let pinkGlow = ShadowStyle(color: Color.brandPink.opacity(0.4), radius: 14, x: 0, y: 4)
        static let pinkFab = ShadowStyle(color: Color.brandPink.opacity(0.5), radius: 30, x: 0, y: 12)
    }
}

struct ShadowStyle {
    let color: Color
    let radius: CGFloat
    let x: CGFloat
    let y: CGFloat
}

extension View {
    func paybitchShadow(_ s: ShadowStyle) -> some View {
        shadow(color: s.color, radius: s.radius, x: s.x, y: s.y)
    }
}

// MARK: - Typography (Space Grotesk)

import CoreText

extension Font {
    /// Space Grotesk (bundled variable font), with system fallback if registration failed.
    /// Letter spacing and weight emphasis are handled via `.tracking()` / `.weight()` modifiers.
    static func spaceGrotesk(_ size: CGFloat, weight: Font.Weight = .regular) -> Font {
        if let name = PaybitchFonts.psName {
            return .custom(name, size: size).weight(weight)
        }
        return .system(size: size, weight: weight, design: .default)
    }
}

/// Runtime font registration. Call `PaybitchFonts.register()` once on app launch.
/// `psName` is written exactly once during `App.init` (main thread) before any
/// view bodies read it; `nonisolated(unsafe)` is sound under that ordering.
enum PaybitchFonts {
    /// PostScript name of the registered font (nil if registration failed).
    nonisolated(unsafe) private(set) static var psName: String?

    static func register() {
        guard psName == nil else { return }
        let candidates = [
            ("SpaceGrotesk-VariableFont_wght", "ttf"),
            ("SpaceGrotesk", "ttf"),
        ]
        for (name, ext) in candidates {
            guard let url = Bundle.main.url(forResource: name, withExtension: ext) else { continue }
            var error: Unmanaged<CFError>?
            CTFontManagerRegisterFontsForURL(url as CFURL, .process, &error)
            if let descs = CTFontManagerCreateFontDescriptorsFromURL(url as CFURL) as? [CTFontDescriptor],
               let first = descs.first,
               let ps = CTFontDescriptorCopyAttribute(first, kCTFontNameAttribute) as? String {
                psName = ps
                return
            }
        }
    }
}

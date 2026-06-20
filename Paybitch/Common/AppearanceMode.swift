//
//  AppearanceMode.swift
//  Paybitch
//
//  User-selectable color scheme. Stored as a raw String in UserDefaults
//  so SwiftUI's `@AppStorage` can bind it directly without a wrapper.
//

import SwiftUI

enum AppearanceMode: String, CaseIterable, Identifiable {
    case system
    case light
    case dark

    var id: String { rawValue }

    var label: String {
        switch self {
        case .system: String(localized: "System")
        case .light:  String(localized: "Light")
        case .dark:   String(localized: "Dark")
        }
    }

    var systemImage: String {
        switch self {
        case .system: "circle.lefthalf.filled"
        case .light:  "sun.max"
        case .dark:   "moon.fill"
        }
    }

    /// nil = follow system; otherwise force the chosen scheme.
    var preferredColorScheme: ColorScheme? {
        switch self {
        case .system: nil
        case .light:  .light
        case .dark:   .dark
        }
    }
}

extension View {
    /// Applies the user's chosen appearance to this view tree. Needed on every
    /// sheet/cover: `.preferredColorScheme` set on the app root does NOT
    /// propagate into modally-presented sheets, so they'd otherwise ignore the
    /// Light/Dark choice and only the main screen would update.
    func paybitchAppearance() -> some View {
        modifier(PaybitchAppearanceModifier())
    }
}

private struct PaybitchAppearanceModifier: ViewModifier {
    @AppStorage(AppStorageKey.appearance) private var raw = AppearanceMode.system.rawValue

    func body(content: Content) -> some View {
        content.preferredColorScheme((AppearanceMode(rawValue: raw) ?? .system).preferredColorScheme)
    }
}

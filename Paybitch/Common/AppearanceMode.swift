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

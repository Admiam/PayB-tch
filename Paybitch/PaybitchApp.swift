//
//  PaybitchApp.swift
//  Paybitch
//

import SwiftUI

@main
struct PaybitchApp: App {
    @State private var modelData = ModelData()
    @AppStorage(AppStorageKey.appearance) private var appearanceRaw: String = AppearanceMode.system.rawValue

    init() {
        PaybitchFonts.register()
    }

    private var appearance: AppearanceMode {
        AppearanceMode(rawValue: appearanceRaw) ?? .system
    }

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environment(modelData)
                .preferredColorScheme(appearance.preferredColorScheme)
                .tint(Paybitch.pink)
        }
    }
}

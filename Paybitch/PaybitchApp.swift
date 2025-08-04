//
//  PaybitchApp.swift
//  Paybitch
//
//  Created by Adam Míka on 01.08.2025.
//

import SwiftUI

@main
struct PaybitchApp: App {
    @StateObject private var modelData = ModelData()

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(modelData)
        }
    }
}

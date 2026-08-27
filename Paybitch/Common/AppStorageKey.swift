//
//  AppStorageKey.swift
//  Paybitch
//
//  Centralized string keys for `@AppStorage`. Avoids stringly-typed typos.
//

import Foundation

enum AppStorageKey {
    static let hasOnboarded = "paybitch.hasOnboarded"
    static let currentUserId = "paybitch.currentUserId"
    static let appearance = "paybitch.appearance"
}

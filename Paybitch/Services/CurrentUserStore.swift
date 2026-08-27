//
//  CurrentUserStore.swift
//  Paybitch
//
//  Persists which Member.id is the active "me". UserDefaults now,
//  swap with Keychain or remote profile when auth lands.
//

import Foundation

protocol CurrentUserStore: Sendable {
    func get() -> String?
    func set(_ id: String?)
}

struct UserDefaultsCurrentUserStore: CurrentUserStore {
    private let key = AppStorageKey.currentUserId
    func get() -> String? { UserDefaults.standard.string(forKey: key) }
    func set(_ id: String?) {
        if let id { UserDefaults.standard.set(id, forKey: key) }
        else { UserDefaults.standard.removeObject(forKey: key) }
    }
}

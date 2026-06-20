//
//  PaybitchLogger.swift
//  Paybitch
//
//  Single source of truth for the OSLog subsystem string.
//

import Foundation
import OSLog

enum PaybitchLog {
    static let subsystem = "com.paybitch"

    static func make(_ category: String) -> Logger {
        Logger(subsystem: subsystem, category: category)
    }
}

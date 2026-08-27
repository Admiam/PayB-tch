//
//  DateFormatters.swift
//  Paybitch
//
//  Cached, thread-safe formatters. DateFormatter alloc is expensive
//  and we hit it from list rows and CRUD.
//

import Foundation

enum PaybitchDate {
    /// `yyyy-MM-dd` in UTC. Used as the canonical wire/storage format.
    /// `DateFormatter` is documented as thread-safe for reading once configured.
    static let isoDay: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd"
        f.timeZone = TimeZone(identifier: "UTC")
        f.locale = Locale(identifier: "en_US_POSIX")
        return f
    }()

    /// User-facing medium date in current locale.
    static let mediumDisplay: DateFormatter = {
        let f = DateFormatter()
        f.dateStyle = .medium
        f.timeStyle = .none
        return f
    }()

    /// Relative time formatter (e.g. "2 h ago"), abbreviated.
    nonisolated(unsafe) static let relative: RelativeDateTimeFormatter = {
        let f = RelativeDateTimeFormatter()
        f.unitsStyle = .abbreviated
        return f
    }()

    static func todayISO(_ date: Date = .now) -> String {
        isoDay.string(from: date)
    }

    static func parseISO(_ s: String) -> Date? {
        isoDay.date(from: s)
    }
}

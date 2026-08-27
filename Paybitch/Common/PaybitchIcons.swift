//
//  PaybitchIcons.swift
//  Paybitch
//
//  Maps expense titles to SF Symbol names for category icons.
//

import Foundation

enum PaybitchIcon {
    /// Resolves a symbol for an expense — explicit override wins, else title-based guess.
    static func symbol(for expense: Expense) -> String {
        if let s = expense.iconSymbol, !s.isEmpty { return s }
        return symbol(forTitle: expense.title)
    }

    /// Best-effort SF Symbol guess from an expense title.
    static func symbol(forTitle title: String) -> String {
        let s = title.lowercased()
        if s.contains("pizza") || s.contains("burger") || s.contains("sushi") || s.contains("lunch")
            || s.contains("dinner") || s.contains("food") || s.contains("restaurant") || s.contains("breakfast") {
            return "fork.knife"
        }
        if s.contains("coffee") || s.contains("café") || s.contains("cafe") || s.contains("espresso") {
            return "cup.and.saucer.fill"
        }
        if s.contains("beer") || s.contains("bar") || s.contains("drinks") || s.contains("pub")
            || s.contains("wine") || s.contains("cocktail") {
            return "mug.fill"
        }
        if s.contains("gas") || s.contains("fuel") || s.contains("petrol") || s.contains("benzín") {
            return "fuelpump.fill"
        }
        if s.contains("grocer") || s.contains("market") || s.contains("shop") || s.contains("store") {
            return "cart.fill"
        }
        if s.contains("hotel") || s.contains("airbnb") || s.contains("stay") || s.contains("rent") {
            return "bed.double.fill"
        }
        if s.contains("snack") || s.contains("popcorn") || s.contains("movie") || s.contains("cinema")
            || s.contains("film") {
            return "popcorn.fill"
        }
        if s.contains("uber") || s.contains("taxi") || s.contains("ride") || s.contains("train")
            || s.contains("bus") || s.contains("flight") || s.contains("travel") {
            return "tram.fill"
        }
        if s.contains("ticket") || s.contains("event") || s.contains("concert") {
            return "ticket.fill"
        }
        return "wallet.bifold.fill"
    }

    /// Curated SF Symbol catalog for the picker, grouped by category.
    static let categories: [Category] = [
        .init(label: "Food & drink", symbols: [
            "fork.knife", "cup.and.saucer.fill", "mug.fill", "wineglass.fill",
            "takeoutbag.and.cup.and.straw.fill", "carrot.fill", "birthday.cake.fill",
            "popcorn.fill"
        ]),
        .init(label: "Transport", symbols: [
            "car.fill", "fuelpump.fill", "tram.fill", "bus.fill",
            "airplane", "bicycle", "ferry.fill", "scooter"
        ]),
        .init(label: "Shopping", symbols: [
            "cart.fill", "bag.fill", "gift.fill", "tshirt.fill",
            "shippingbox.fill", "creditcard.fill"
        ]),
        .init(label: "Home", symbols: [
            "house.fill", "bed.double.fill", "lightbulb.fill", "washer.fill",
            "wrench.and.screwdriver.fill", "key.fill"
        ]),
        .init(label: "Fun", symbols: [
            "ticket.fill", "gamecontroller.fill", "music.note", "film.fill",
            "sparkles", "party.popper.fill"
        ]),
        .init(label: "Health & pets", symbols: [
            "cross.case.fill", "pills.fill", "heart.fill", "figure.run",
            "pawprint.fill", "dumbbell.fill"
        ]),
        .init(label: "Misc", symbols: [
            "wallet.bifold.fill", "dollarsign.circle.fill", "doc.text.fill",
            "graduationcap.fill", "briefcase.fill", "questionmark.circle.fill"
        ]),
    ]

    struct Category: Hashable, Sendable {
        let label: String
        let symbols: [String]
    }
}

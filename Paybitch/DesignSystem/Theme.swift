//
//  Theme.swift
//  Paybitch
//
//  Centralized, neutral design tokens. No view should hardcode palette,
//  spacing, or radii — everything routes through here so the look stays
//  consistent and easy to retune.
//

import SwiftUI

enum Theme {

    // MARK: Neutral palette

    /// App-wide canvas. Neutral grouped background that adapts to light/dark.
    static let background = Color(.systemGroupedBackground)

    /// Raised surface tone used behind glass when extra contrast is needed.
    static let surface = Color(.secondarySystemGroupedBackground)

    /// Monochrome accent (graphite ↔ off-white). Defined in the asset catalog
    /// so prominent controls stay neutral instead of system blue.
    static let accent = Color.accentColor

    // MARK: Spacing scale

    enum Spacing {
        static let xs: CGFloat = 4
        static let sm: CGFloat = 8
        static let md: CGFloat = 12
        static let lg: CGFloat = 20
        static let xl: CGFloat = 32
    }

    // MARK: Corner radii

    enum Radius {
        static let sm: CGFloat = 12
        static let md: CGFloat = 18
        static let lg: CGFloat = 24
    }

    // MARK: Control sizing

    enum Size {
        /// Square edge for compact icon controls (e.g. the add button).
        static let control: CGFloat = 64
        /// Height of the primary horizontal bar controls.
        static let bar: CGFloat = 64
    }

    // MARK: Motion

    enum Motion {
        static let snappy = Animation.snappy(duration: 0.28)
    }
}

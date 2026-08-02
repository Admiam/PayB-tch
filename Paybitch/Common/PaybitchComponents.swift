//
//  PaybitchComponents.swift
//  Paybitch
//
//  Reusable design-system components: wordmark, badge, chip, tile, buttons.
//  All tappable controls route through the unified Liquid Glass button style.
//

import SwiftUI

// MARK: - Wordmark

struct PaybitchWordmark: View {
    var size: CGFloat = 26
    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Text("paybitch")
                .font(.spaceGrotesk(size, weight: .heavy))
                .tracking(-1)
                .foregroundStyle(Paybitch.textPrimary)
            Circle()
                .fill(Paybitch.pink)
                .frame(width: 6, height: 6)
                .offset(y: -2)
        }
    }
}

// MARK: - Badge

/// Small neutral pill used for metadata (split type, date, …).
struct StickerBadge: View {
    let text: String
    var rotation: Double = 0

    var body: some View {
        Text(text.uppercased())
            .font(.spaceGrotesk(11, weight: .heavy))
            .tracking(0.6)
            .foregroundStyle(Paybitch.textPrimary)
            .padding(.horizontal, 10)
            .padding(.vertical, 5)
            .background(Capsule().fill(Paybitch.chipBg))
            .overlay(Capsule().stroke(Paybitch.divider, lineWidth: 1))
            .rotationEffect(.degrees(rotation))
    }
}

// MARK: - Group chip

struct GroupChip: View {
    let name: String
    let memberCount: Int
    let isActive: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 6) {
                Text(name)
                    .font(.spaceGrotesk(14, weight: .bold))
                    .tracking(-0.2)
                Text("\(memberCount)")
                    .font(.spaceGrotesk(11, weight: .bold))
                    .opacity(0.6)
            }
            .foregroundStyle(isActive ? Color.white : Paybitch.textPrimary)
            .padding(.horizontal, 14)
            .padding(.vertical, 10)
        }
        .buttonStyle(PaybitchGlassButton(kind: isActive ? .accent : .neutral, shape: Capsule()))
        .animation(.easeOut(duration: 0.2), value: isActive)
    }
}

struct NewGroupChip: View {
    let action: () -> Void
    var body: some View {
        Button(action: action) {
            Text("+ New")
                .font(.spaceGrotesk(14, weight: .bold))
                .foregroundStyle(Paybitch.pink)
                .padding(.horizontal, 16)
                .padding(.vertical, 10)
        }
        .buttonStyle(PaybitchGlassButton(kind: .neutral, shape: Capsule()))
    }
}

// MARK: - Section header

struct PaybitchSectionHeader<Trailing: View>: View {
    let title: String
    var badge: String?
    @ViewBuilder var trailing: () -> Trailing

    var body: some View {
        HStack(spacing: 8) {
            Text(title)
                .font(.spaceGrotesk(22, weight: .bold))
                .tracking(-0.5)
                .foregroundStyle(Paybitch.textPrimary)
            if let badge {
                Text(badge)
                    .font(.spaceGrotesk(11, weight: .bold))
                    .foregroundStyle(Paybitch.pink)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 3)
                    .background(Capsule().fill(Paybitch.pink.opacity(0.15)))
            }
            Spacer()
            trailing()
        }
        .padding(.horizontal, 24)
        .padding(.top, 20)
        .padding(.bottom, 10)
    }
}

extension PaybitchSectionHeader where Trailing == EmptyView {
    init(title: String, badge: String? = nil) {
        self.title = title
        self.badge = badge
        self.trailing = { EmptyView() }
    }
}

// MARK: - Sheet field group

/// A grouped card with optional caption label, used inside redesigned sheets.
struct PaybitchFieldGroup<Content: View>: View {
    let label: String?
    @ViewBuilder var content: () -> Content

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let label {
                Text(label.uppercased())
                    .font(.spaceGrotesk(11, weight: .bold))
                    .tracking(1.0)
                    .foregroundStyle(Paybitch.textMuted)
                    .padding(.horizontal, 8)
            }
            VStack(spacing: 0) {
                content()
            }
            .background(
                RoundedRectangle(cornerRadius: Paybitch.radiusCard, style: .continuous)
                    .fill(Paybitch.card)
            )
            .clipShape(RoundedRectangle(cornerRadius: Paybitch.radiusCard, style: .continuous))
        }
    }
}

extension PaybitchFieldGroup where Content == EmptyView {
    init(label: String?) {
        self.label = label
        self.content = { EmptyView() }
    }
}

/// Inline row with a primary label on the left and trailing accessory on the right.
struct PaybitchInlineRow<Trailing: View>: View {
    let label: String
    var divider: Bool = true
    let action: (() -> Void)?
    @ViewBuilder var trailing: () -> Trailing

    var body: some View {
        VStack(spacing: 0) {
            Button(action: { action?() }) {
                HStack(spacing: 12) {
                    Text(label)
                        .font(.spaceGrotesk(16, weight: .semibold))
                        .foregroundStyle(Paybitch.textPrimary)
                    Spacer()
                    trailing()
                        .font(.spaceGrotesk(16, weight: .bold))
                        .foregroundStyle(Paybitch.pink)
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 14)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .disabled(action == nil)
            if divider { Divider().background(Paybitch.divider) }
        }
    }
}

// MARK: - Unified buttons (Liquid Glass)

/// Primary action button (Save / Done). Pink-tinted glass capsule.
struct PaybitchPrimaryButton: View {
    let title: String
    var disabled: Bool = false
    let action: () -> Void
    var body: some View {
        Button(action: action) {
            Text(title)
                .font(.spaceGrotesk(15, weight: .heavy))
                .foregroundStyle(.white)
                .padding(.horizontal, 20)
                .padding(.vertical, 11)
        }
        .buttonStyle(PaybitchGlassButton(kind: .accent, shape: Capsule(), dimmed: disabled))
        .disabled(disabled)
    }
}

/// Plain text action (e.g. "Edit").
struct PaybitchTextButton: View {
    let title: String
    var foreground: Color = Paybitch.pink
    let action: () -> Void
    var body: some View {
        Button(action: action) {
            Text(title)
                .font(.spaceGrotesk(15, weight: .heavy))
                .foregroundStyle(foreground)
        }
        .buttonStyle(.plain)
    }
}

/// Circular close button used in sheet toolbars. Neutral glass.
struct PaybitchCloseButton: View {
    let action: () -> Void
    var body: some View {
        Button(action: action) {
            Image(systemName: "xmark")
                .font(.system(size: 13, weight: .heavy))
                .foregroundStyle(Paybitch.textPrimary)
                .frame(width: 34, height: 34)
        }
        .buttonStyle(PaybitchGlassButton(kind: .neutral, shape: Circle()))
        .accessibilityLabel(Text("Close"))
    }
}

/// Full-width destructive action (delete). Neutral glass with red label —
/// replaces the copy-pasted outlined delete buttons across the sheets.
struct PaybitchDestructiveButton: View {
    let title: String
    var systemImage: String = "trash"
    let action: () -> Void
    var body: some View {
        Button(action: action) {
            Label(title, systemImage: systemImage)
                .font(.spaceGrotesk(14, weight: .bold))
                .foregroundStyle(Paybitch.negative)
                .frame(maxWidth: .infinity)
                .padding(.vertical, 14)
        }
        .buttonStyle(
            PaybitchGlassButton(
                kind: .neutral,
                shape: RoundedRectangle(cornerRadius: Paybitch.radiusButton, style: .continuous)
            )
        )
    }
}

// MARK: - Tile

/// Stat tile on a neutral Liquid Glass surface. The amount carries the only
/// color (semantic green/red or the pink accent) so the surface stays neutral.
struct PaybitchTile<Icon: View>: View {
    let label: String
    let amount: String
    let currencySymbol: String
    let sub: String?
    /// 0...1 — fills bar viz under amount.
    var ratio: Double = 0
    /// Color of the headline amount (semantic or accent).
    var amountColor: Color = Paybitch.textPrimary
    var isBig: Bool = false
    @ViewBuilder var icon: () -> Icon

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text(label.uppercased())
                    .font(.spaceGrotesk(12, weight: .bold))
                    .tracking(0.5)
                    .foregroundStyle(Paybitch.textMuted)
                Spacer()
                icon()
                    .foregroundStyle(amountColor.opacity(0.85))
            }

            Spacer(minLength: 0)

            VStack(alignment: .leading, spacing: 0) {
                HStack(alignment: .firstTextBaseline, spacing: 4) {
                    Text(amount)
                        .font(.spaceGrotesk(isBig ? 42 : 28, weight: .bold))
                        .tracking(-1)
                        .lineLimit(1)
                        .minimumScaleFactor(0.6)
                    Text(currencySymbol)
                        .font(.spaceGrotesk(isBig ? 24 : 16, weight: .bold))
                        .opacity(0.6)
                }
                .foregroundStyle(amountColor)

                if ratio > 0 {
                    GeometryReader { g in
                        ZStack(alignment: .leading) {
                            Capsule().fill(Paybitch.textMuted.opacity(0.25))
                            Capsule()
                                .fill(amountColor.opacity(0.7))
                                .frame(width: max(0, min(1, ratio)) * g.size.width)
                        }
                    }
                    .frame(height: 4)
                    .padding(.top, 10)
                }

                if let sub {
                    Text(sub)
                        .font(.spaceGrotesk(11, weight: .semibold))
                        .foregroundStyle(Paybitch.textMuted)
                        .padding(.top, 6)
                }
            }
        }
        .padding(isBig ? 20 : 16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .frame(minHeight: isBig ? 130 : 110)
        .glassEffect(
            .regular,
            in: RoundedRectangle(cornerRadius: Paybitch.radiusTile, style: .continuous)
        )
    }
}

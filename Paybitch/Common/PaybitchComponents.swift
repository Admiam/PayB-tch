//
//  PaybitchComponents.swift
//  Paybitch
//
//  Reusable design-system components: wordmark, sticker badge, chip, tile.
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

// MARK: - Sticker badge

struct StickerBadge: View {
    let text: String
    var color: Color = Paybitch.pink
    var rotation: Double = -3

    private var fg: Color { color == Paybitch.lime ? Color(red: 0.102, green: 0.039, blue: 0.094) : .white }

    var body: some View {
        Text(text.uppercased())
            .font(.spaceGrotesk(11, weight: .heavy))
            .tracking(0.6)
            .foregroundStyle(fg)
            .padding(.horizontal, 9)
            .padding(.vertical, 4)
            .background(Capsule().fill(color))
            .shadow(color: .black.opacity(0.2), radius: 0, x: 0, y: 2)
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
            HStack(spacing: 8) {
                Circle()
                    .fill(isActive ? .white : Paybitch.pink)
                    .frame(width: 6, height: 6)
                Text(name)
                    .font(.spaceGrotesk(14, weight: .bold))
                    .tracking(-0.2)
                Text("· \(memberCount)")
                    .font(.spaceGrotesk(11, weight: .bold))
                    .opacity(0.6)
            }
            .foregroundStyle(isActive ? .white : Paybitch.textPrimary)
            .padding(.horizontal, 14)
            .padding(.vertical, 10)
            .background(
                Capsule().fill(isActive ? Paybitch.pink : Paybitch.chipBg)
            )
            .scaleEffect(isActive ? 1.04 : 1)
            .shadow(color: isActive ? Paybitch.pink.opacity(0.4) : .clear, radius: 14, x: 0, y: 4)
        }
        .buttonStyle(.plain)
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
                .background(
                    Capsule()
                        .stroke(Paybitch.pink.opacity(0.5), style: StrokeStyle(lineWidth: 1.5, dash: [4, 4]))
                )
        }
        .buttonStyle(.plain)
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

/// Pink primary action button used in sheet toolbars and modals.
struct PaybitchPrimaryButton: View {
    let title: String
    var disabled: Bool = false
    let action: () -> Void
    var body: some View {
        Button(action: action) {
            Text(title)
                .font(.spaceGrotesk(15, weight: .heavy))
                .foregroundStyle(.white)
                .padding(.horizontal, 18)
                .padding(.vertical, 10)
                .background(Capsule().fill(Paybitch.pink))
                .opacity(disabled ? 0.4 : 1)
        }
        .buttonStyle(.plain)
        .paybitchShadow(Paybitch.Shadow.pinkGlow)
        .disabled(disabled)
    }
}

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

struct PaybitchCloseButton: View {
    var foreground: Color = .white
    var background: Color = Paybitch.pink
    let action: () -> Void
    var body: some View {
        Button(action: action) {
            Image(systemName: "xmark")
                .font(.system(size: 13, weight: .heavy))
                .foregroundStyle(foreground)
                .frame(width: 30, height: 30)
                .background(Circle().fill(background))
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .paybitchShadow(Paybitch.Shadow.pinkGlow)
        .accessibilityLabel(Text("Close"))
    }
}

// MARK: - Tile

struct PaybitchTile<Icon: View>: View {
    let label: String
    let amount: String
    let currencySymbol: String
    let sub: String?
    /// 0...1 — fills bar viz under amount.
    var ratio: Double = 0
    var background: Color
    var foreground: Color
    var isBig: Bool = false
    @ViewBuilder var icon: () -> Icon

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text(label.uppercased())
                    .font(.spaceGrotesk(12, weight: .bold))
                    .tracking(0.5)
                    .foregroundStyle(foreground.opacity(0.75))
                Spacer()
                icon()
                    .foregroundStyle(foreground.opacity(0.55))
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
                .foregroundStyle(foreground)

                if ratio > 0 {
                    GeometryReader { g in
                        ZStack(alignment: .leading) {
                            Capsule().fill(.black.opacity(0.15))
                            Capsule()
                                .fill(foreground.opacity(0.5))
                                .frame(width: max(0, min(1, ratio)) * g.size.width)
                        }
                    }
                    .frame(height: 4)
                    .padding(.top, 10)
                }

                if let sub {
                    Text(sub)
                        .font(.spaceGrotesk(11, weight: .semibold))
                        .foregroundStyle(foreground.opacity(0.6))
                        .padding(.top, 6)
                }
            }
        }
        .padding(isBig ? 20 : 16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .frame(minHeight: isBig ? 130 : 110)
        .background(
            RoundedRectangle(cornerRadius: Paybitch.radiusTile, style: .continuous).fill(background)
        )
        .paybitchShadow(Paybitch.Shadow.tile)
    }
}

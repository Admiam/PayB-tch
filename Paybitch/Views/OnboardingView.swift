//
//  OnboardingView.swift
//  Paybitch
//
//  Three-step onboarding: brand intro → profile name → first group.
//  Final step uses a pink/lime gradient for celebration.
//

import SwiftUI

struct OnboardingView: View {
    @Environment(ModelData.self) private var model
    @Environment(\.dismiss) private var dismiss

    @State private var step: Int = 0
    @State private var profileName: String = ""
    @State private var groupName: String = ""
    @State private var groupCurrency: Currency = .default
    @State private var saving = false

    @AppStorage(AppStorageKey.hasOnboarded) private var hasOnboarded: Bool = false

    private var profileValid: Bool { !profileName.trimmingCharacters(in: .whitespaces).isEmpty }
    private var groupValid: Bool { !groupName.trimmingCharacters(in: .whitespaces).isEmpty }

    var body: some View {
        ZStack {
            background.ignoresSafeArea()
            VStack(alignment: .leading, spacing: 24) {
                content
                Spacer()
                primaryAction
            }
            .padding(24)
            .padding(.top, 60)
        }
        .interactiveDismissDisabled()
        .paybitchAppearance()
    }

    @ViewBuilder
    private var background: some View {
        ZStack {
            Paybitch.bg
            RadialGradient(
                colors: [Paybitch.pink.opacity(step == 2 ? 0.22 : 0.14), .clear],
                center: .topTrailing, startRadius: 0, endRadius: 500
            )
        }
    }

    @ViewBuilder
    private var content: some View {
        switch step {
        case 0: introStep
        case 1: profileStep
        default: groupStep
        }
    }

    private var introStep: some View {
        VStack(alignment: .leading, spacing: 32) {
            Text("pay\nbitch.")
                .font(.spaceGrotesk(60, weight: .heavy))
                .tracking(-2)
                .lineSpacing(-12)
                .foregroundStyle(Paybitch.pink)
                .rotationEffect(.degrees(-3))

            VStack(alignment: .leading, spacing: 12) {
                Text("Split bills.\nLose no friends.")
                    .font(.spaceGrotesk(22, weight: .bold))
                    .tracking(-0.5)
                    .foregroundStyle(Paybitch.textPrimary)
                Text("Tell us your name and we'll start a group with your crew.")
                    .font(.spaceGrotesk(15))
                    .foregroundStyle(Paybitch.textMuted)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var profileStep: some View {
        VStack(alignment: .leading, spacing: 24) {
            Text("Step 1 of 2")
                .font(.spaceGrotesk(11, weight: .bold))
                .tracking(1.5)
                .foregroundStyle(Paybitch.textMuted)
                .textCase(.uppercase)
            Text("What's your name?")
                .font(.spaceGrotesk(36, weight: .heavy))
                .tracking(-1)
                .foregroundStyle(Paybitch.textPrimary)
            TextField(text: $profileName, prompt: Text("Type your name").foregroundStyle(Paybitch.textMuted)) {
                Text("")
            }
            .paybitchNoAutocorrect()
            .font(.spaceGrotesk(22, weight: .bold))
            .foregroundStyle(Paybitch.textPrimary)
            .padding(22)
            .background(
                RoundedRectangle(cornerRadius: 18, style: .continuous).fill(Paybitch.card)
            )
        }
    }

    private var groupStep: some View {
        VStack(alignment: .leading, spacing: 24) {
            Text("Step 2 of 2")
                .font(.spaceGrotesk(11, weight: .bold))
                .tracking(1.5)
                .foregroundStyle(Paybitch.textMuted)
                .textCase(.uppercase)
            Text("Hey \(profileName).\nName your first group.")
                .font(.spaceGrotesk(36, weight: .heavy))
                .tracking(-1)
                .foregroundStyle(Paybitch.textPrimary)
            TextField(text: $groupName, prompt: Text("e.g. Roommates").foregroundStyle(Paybitch.textMuted)) {
                Text("")
            }
            .paybitchNoAutocorrect()
            .font(.spaceGrotesk(22, weight: .bold))
            .foregroundStyle(Paybitch.textPrimary)
            .padding(22)
            .background(
                RoundedRectangle(cornerRadius: 18, style: .continuous).fill(Paybitch.card)
            )

            HStack(spacing: 8) {
                ForEach(Currency.allCases) { c in
                    let selected = groupCurrency == c
                    Button {
                        groupCurrency = c
                    } label: {
                        Text("\(c.rawValue) \(c.symbol)")
                            .font(.spaceGrotesk(13, weight: .bold))
                            .foregroundStyle(selected ? .white : Paybitch.textPrimary)
                            .padding(.horizontal, 12)
                            .padding(.vertical, 8)
                    }
                    .buttonStyle(PaybitchGlassButton(kind: selected ? .accent : .neutral, shape: Capsule()))
                }
            }
        }
    }

    @ViewBuilder
    private var primaryAction: some View {
        switch step {
        case 0:
            bigButton(title: "Let's go →", filled: true) { step = 1 }

        case 1:
            bigButton(title: "Next →", filled: true, disabled: !profileValid) {
                Task { await saveProfile() }
            }

        default:
            VStack(spacing: 8) {
                bigButton(
                    title: saving ? "Saving…" : "Take me in 🎉",
                    filled: false,
                    disabled: !groupValid || saving
                ) {
                    Task { await saveGroupAndFinish() }
                }
                Button("Skip for now") { finish() }
                    .font(.spaceGrotesk(13, weight: .bold))
                    .foregroundStyle(Paybitch.textMuted)
            }
        }
    }

    @ViewBuilder
    private func bigButton(title: String, filled: Bool, disabled: Bool = false, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .font(.spaceGrotesk(18, weight: .heavy))
                .tracking(0.2)
                .foregroundStyle(filled ? .white : Paybitch.pink)
                .frame(maxWidth: .infinity)
                .padding(.vertical, 20)
        }
        .buttonStyle(
            PaybitchGlassButton(
                kind: filled ? .accent : .neutral,
                shape: RoundedRectangle(cornerRadius: 22, style: .continuous),
                dimmed: disabled
            )
        )
        .disabled(disabled)
    }

    private func saveProfile() async {
        guard profileValid, !saving else { return }
        saving = true
        defer { saving = false }

        let trimmed = profileName.trimmingCharacters(in: .whitespaces)
        if let me = model.currentUser {
            let updated = Member(id: me.id, name: trimmed, imageUrl: me.imageUrl)
            await model.updateMember(updated)
        } else {
            let new = Member(id: UUID().uuidString, name: trimmed, imageUrl: nil)
            if let saved = await model.addMember(new) {
                model.setCurrentUser(saved.id)
            }
        }
        step = 2
    }

    private func saveGroupAndFinish() async {
        guard groupValid, !saving else { return }
        saving = true
        defer { saving = false }

        let trimmed = groupName.trimmingCharacters(in: .whitespaces)
        let memberIds = model.currentUserId.map { [$0] } ?? []
        let g = Group(name: trimmed, memberIds: memberIds, defaultCurrency: groupCurrency.rawValue)
        await model.addGroup(g)
        finish()
    }

    private func finish() {
        hasOnboarded = true
        dismiss()
    }
}

//
//  SettingsSheet.swift
//  Paybitch
//

import SwiftUI

struct SettingsSheet: View {
    @Environment(ModelData.self) private var model
    @Environment(\.dismiss) private var dismiss

    @AppStorage(AppStorageKey.appearance) private var appearanceRaw: String = AppearanceMode.system.rawValue

    @State private var addingMember = false
    @State private var editingMember: Member?
    @State private var profilePickerShown = false

    private var appearanceBinding: Binding<AppearanceMode> {
        Binding(
            get: { AppearanceMode(rawValue: appearanceRaw) ?? .system },
            set: { appearanceRaw = $0.rawValue }
        )
    }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: 18) {
                    PaybitchFieldGroup(label: "Appearance") {
                        appearanceSegmented.padding(8)
                    }

                    PaybitchFieldGroup(label: "Profile") {
                        profileRow
                    }

                    PaybitchFieldGroup(label: "Members") {
                        VStack(spacing: 0) {
                            ForEach(Array(model.members.enumerated()), id: \.element.id) { idx, m in
                                memberRow(m, isLast: idx == model.members.count - 1 && true == false)
                            }
                            Divider().background(Paybitch.divider)
                            Button { addingMember = true } label: {
                                HStack(spacing: 8) {
                                    plusBadge
                                    Text("New member")
                                        .font(.spaceGrotesk(15, weight: .bold))
                                        .foregroundStyle(Paybitch.pink)
                                    Spacer()
                                }
                                .padding(.horizontal, 16)
                                .padding(.vertical, 14)
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                        }
                    }

                    PaybitchFieldGroup(label: "Data") {
                        VStack(spacing: 0) {
                            if let url = exportURL {
                                ShareLink(item: url) {
                                    HStack(spacing: 12) {
                                        Text("Export as JSON")
                                            .font(.spaceGrotesk(16, weight: .semibold))
                                            .foregroundStyle(Paybitch.textPrimary)
                                        Spacer()
                                        chevron
                                    }
                                    .padding(.horizontal, 16)
                                    .padding(.vertical, 14)
                                }
                                .buttonStyle(.plain)
                            }
                            Divider().background(Paybitch.divider)
                            HStack(spacing: 12) {
                                Text("Version")
                                    .font(.spaceGrotesk(16, weight: .semibold))
                                    .foregroundStyle(Paybitch.textPrimary)
                                Spacer()
                                Text(appVersion)
                                    .font(.spaceGrotesk(14, weight: .semibold))
                                    .foregroundStyle(Paybitch.textMuted)
                            }
                            .padding(.horizontal, 16)
                            .padding(.vertical, 14)
                        }
                    }

                    wordmarkBlock
                        .padding(.top, 4)
                }
                .padding(.horizontal, 16)
                .padding(.top, 8)
                .padding(.bottom, 60)
            }
            .background(Paybitch.bg.ignoresSafeArea())
            .navigationTitle("Settings")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    PaybitchPrimaryButton(title: "Done") { dismiss() }
                }
            }
            .sheet(isPresented: $addingMember) { AddMemberSheet(editing: nil) }
            .sheet(item: $editingMember) { m in AddMemberSheet(editing: m) }
        }
    }

    @ViewBuilder
    private var appearanceSegmented: some View {
        let options = AppearanceMode.allCases
        HStack(spacing: 4) {
            ForEach(options) { mode in
                let active = appearanceBinding.wrappedValue == mode
                Button {
                    appearanceBinding.wrappedValue = mode
                } label: {
                    Text(mode.label)
                        .font(.spaceGrotesk(13, weight: .bold))
                        .foregroundStyle(active ? .white : Paybitch.textPrimary)
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 8)
                        .background(Capsule().fill(active ? Paybitch.pink : Color.clear))
                }
                .buttonStyle(.plain)
            }
        }
        .padding(4)
        .background(Capsule().fill(Paybitch.chipBg))
    }

    @ViewBuilder
    private var profileRow: some View {
        if model.members.isEmpty {
            Text("No members yet — add someone first.")
                .font(.spaceGrotesk(14))
                .foregroundStyle(Paybitch.textMuted)
                .padding(16)
                .frame(maxWidth: .infinity, alignment: .leading)
        } else {
            Menu {
                ForEach(model.members) { m in
                    Button(m.name) { model.setCurrentUser(m.id) }
                }
            } label: {
                HStack(spacing: 12) {
                    Text("I am")
                        .font(.spaceGrotesk(16, weight: .semibold))
                        .foregroundStyle(Paybitch.textPrimary)
                    Spacer()
                    Text(model.currentUser?.name ?? "—")
                        .font(.spaceGrotesk(16, weight: .bold))
                        .foregroundStyle(Paybitch.pink)
                    chevron
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 14)
                .contentShape(Rectangle())
            }
        }
    }

    @ViewBuilder
    private func memberRow(_ m: Member, isLast: Bool) -> some View {
        VStack(spacing: 0) {
            Button { editingMember = m } label: {
                HStack(spacing: 12) {
                    PaybitchAvatar(member: m, size: 36, isMe: model.currentUserId == m.id)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(m.name)
                            .font(.spaceGrotesk(15, weight: .bold))
                            .foregroundStyle(Paybitch.pink)
                        if model.currentUserId == m.id {
                            Text("This is me")
                                .font(.spaceGrotesk(11))
                                .foregroundStyle(Paybitch.textMuted)
                        }
                    }
                    Spacer()
                    chevron
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 12)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            if !isLast { Divider().background(Paybitch.divider) }
        }
    }

    private var plusBadge: some View {
        Text("+")
            .font(.spaceGrotesk(18, weight: .heavy))
            .foregroundStyle(Paybitch.pink)
            .frame(width: 32, height: 32)
            .background(Circle().fill(Paybitch.pink.opacity(0.15)))
    }

    private var chevron: some View {
        Image(systemName: "chevron.right")
            .font(.system(size: 12, weight: .bold))
            .foregroundStyle(Paybitch.pink.opacity(0.5))
    }

    private var wordmarkBlock: some View {
        VStack(spacing: 4) {
            Text("paybitch")
                .font(.spaceGrotesk(32, weight: .heavy))
                .tracking(-1)
                .foregroundStyle(
                    LinearGradient(colors: [Paybitch.pink, Paybitch.lime],
                                   startPoint: .leading, endPoint: .trailing)
                )
                .rotationEffect(.degrees(-3))
            Text("v\(appVersion) · sweet & sour math")
                .font(.spaceGrotesk(11))
                .foregroundStyle(Paybitch.textMuted)
        }
        .frame(maxWidth: .infinity)
    }

    private var appVersion: String {
        let v = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "?"
        let b = Bundle.main.infoDictionary?["CFBundleVersion"] as? String ?? "?"
        return "\(v) (\(b))"
    }

    private var exportURL: URL? {
        guard !model.expenses.isEmpty || !model.groups.isEmpty || !model.members.isEmpty else {
            return nil
        }
        let snapshot = ExportSnapshot(
            members: model.members,
            groups: model.groups,
            expenses: model.expenses
        )
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        guard let data = try? encoder.encode(snapshot) else { return nil }

        let stamp = ISO8601DateFormatter().string(from: .now)
            .replacingOccurrences(of: ":", with: "-")
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("paybitch-\(stamp).json")
        try? data.write(to: url, options: .atomic)
        return url
    }
}

private struct ExportSnapshot: Codable {
    let members: [Member]
    let groups: [Group]
    let expenses: [Expense]
    let exportedAt: Date

    init(members: [Member], groups: [Group], expenses: [Expense]) {
        self.members = members
        self.groups = groups
        self.expenses = expenses
        self.exportedAt = .now
    }
}

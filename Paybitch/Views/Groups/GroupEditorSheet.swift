//
//  GroupEditorSheet.swift
//  Paybitch
//
//  Create or edit a group: name, default currency, member roster.
//

import SwiftUI

struct GroupEditorSheet: View {
    @Environment(ModelData.self) private var model
    @Environment(\.dismiss) private var dismiss

    let editing: Group?
    var onCreated: ((Group) -> Void)? = nil

    @State private var name: String = ""
    @State private var currency: Currency = .default
    @State private var selectedMembers: Set<String> = []
    @State private var saving = false
    @State private var addingMember = false
    @State private var showDeleteConfirm = false

    private var canSave: Bool {
        !name.trimmingCharacters(in: .whitespaces).isEmpty
            && !selectedMembers.isEmpty
            && !saving
    }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: 18) {
                    PaybitchFieldGroup(label: "Group") {
                        VStack(spacing: 0) {
                            HStack(spacing: 0) {
                                Text("Name")
                                    .font(.spaceGrotesk(11, weight: .bold))
                                    .tracking(0.8)
                                    .foregroundStyle(Paybitch.textMuted)
                                    .textCase(.uppercase)
                                Spacer()
                            }
                            .padding(.horizontal, 16)
                            .padding(.top, 14)
                            TextField(text: $name, prompt: Text("e.g. Roommates").foregroundStyle(Paybitch.textMuted)) {
                                Text("")
                            }
                            .paybitchNoAutocorrect()
                            .font(.spaceGrotesk(16, weight: .semibold))
                            .foregroundStyle(Paybitch.textPrimary)
                            .padding(.horizontal, 16)
                            .padding(.bottom, 14)
                            Divider().background(Paybitch.divider)
                            Menu {
                                ForEach(Currency.allCases) { c in
                                    Button(c.label) { currency = c }
                                }
                            } label: {
                                HStack(spacing: 12) {
                                    Text("Default currency")
                                        .font(.spaceGrotesk(16, weight: .semibold))
                                        .foregroundStyle(Paybitch.textPrimary)
                                    Spacer()
                                    Text("\(currency.rawValue) \(currency.symbol)")
                                        .font(.spaceGrotesk(16, weight: .bold))
                                        .foregroundStyle(Paybitch.pink)
                                    Image(systemName: "chevron.up.chevron.down")
                                        .font(.system(size: 11, weight: .bold))
                                        .foregroundStyle(Paybitch.pink.opacity(0.5))
                                }
                                .padding(.horizontal, 16)
                                .padding(.vertical, 14)
                                .contentShape(Rectangle())
                            }
                        }
                    }

                    PaybitchFieldGroup(label: "Members (\(selectedMembers.count))") {
                        VStack(spacing: 0) {
                            if model.members.isEmpty {
                                Text("No members yet — add one first.")
                                    .font(.spaceGrotesk(14))
                                    .foregroundStyle(Paybitch.textMuted)
                                    .padding(16)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                            } else {
                                ForEach(Array(model.members.enumerated()), id: \.element.id) { idx, m in
                                    memberRow(m, isLast: idx == model.members.count - 1 && true == false)
                                }
                            }
                            Divider().background(Paybitch.divider)
                            Button { addingMember = true } label: {
                                HStack(spacing: 8) {
                                    Text("+")
                                        .font(.spaceGrotesk(14, weight: .heavy))
                                        .foregroundStyle(Paybitch.pink)
                                        .frame(width: 24, height: 24)
                                        .background(Circle().fill(Paybitch.pink.opacity(0.15)))
                                    Text("New member")
                                        .font(.spaceGrotesk(14, weight: .bold))
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

                    Text("Toggle a member to add or remove them. You're always in.")
                        .font(.spaceGrotesk(12))
                        .foregroundStyle(Paybitch.textMuted)
                        .padding(.horizontal, 16)
                        .frame(maxWidth: .infinity, alignment: .leading)

                    if editing != nil {
                        PaybitchDestructiveButton(title: "Delete group") {
                            showDeleteConfirm = true
                        }
                    }
                }
                .padding(.horizontal, 16)
                .padding(.top, 8)
                .padding(.bottom, 60)
            }
            .background(Paybitch.bg.ignoresSafeArea())
            .paybitchAppearance()
            .navigationTitle(editing == nil ? "New group" : "Edit group")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    PaybitchCloseButton { dismiss() }
                }
                .sharedBackgroundVisibility(.hidden)
                ToolbarItem(placement: .confirmationAction) {
                    PaybitchPrimaryButton(title: "Save", disabled: !canSave) {
                        Task { await save() }
                    }
                }
                .sharedBackgroundVisibility(.hidden)
            }
            .task { setDefaults() }
            .sheet(isPresented: $addingMember) { AddMemberSheet(editing: nil) }
            .confirmationDialog(
                "Delete this group?",
                isPresented: $showDeleteConfirm,
                titleVisibility: .visible
            ) {
                Button("Delete group and expenses", role: .destructive) {
                    Task { await deleteGroup() }
                }
                Button("Cancel", role: .cancel) {}
            }
        }
    }

    @ViewBuilder
    private func memberRow(_ m: Member, isLast: Bool) -> some View {
        let isMe = m.id == model.currentUserId
        let on = isMe || selectedMembers.contains(m.id)
        VStack(spacing: 0) {
            HStack(spacing: 12) {
                PaybitchAvatar(member: m, size: 36, isMe: isMe)
                Text(model.displayName(for: m.id))
                    .font(.spaceGrotesk(15, weight: .bold))
                    .foregroundStyle(Paybitch.textPrimary)
                Spacer()
                pillToggle(on: on, disabled: isMe) {
                    if on { selectedMembers.remove(m.id) } else { selectedMembers.insert(m.id) }
                }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
            if !isLast { Divider().background(Paybitch.divider) }
        }
    }

    private func pillToggle(on: Bool, disabled: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            ZStack(alignment: on ? .trailing : .leading) {
                Capsule()
                    .fill(on ? Paybitch.pink : Color(red: 0.47, green: 0.47, blue: 0.50, opacity: 0.32))
                Circle()
                    .fill(.white)
                    .frame(width: 26, height: 26)
                    .padding(2)
                    .shadow(color: .black.opacity(0.2), radius: 2, y: 1)
            }
            .frame(width: 50, height: 30)
            .opacity(disabled ? 0.5 : 1)
        }
        .buttonStyle(.plain)
        .disabled(disabled)
    }

    private func setDefaults() {
        if let editing {
            name = editing.name
            currency = Currency(rawValue: editing.defaultCurrency.uppercased()) ?? .default
            selectedMembers = Set(editing.memberIds)
        } else {
            currency = .default
        }
        if let me = model.currentUserId { selectedMembers.insert(me) }
    }

    private func save() async {
        guard canSave else { return }
        saving = true
        defer { saving = false }

        let trimmed = name.trimmingCharacters(in: .whitespaces)
        var memberSet = selectedMembers
        if let me = model.currentUserId { memberSet.insert(me) }
        let memberIds = Array(memberSet)

        if let editing {
            let updated = Group(
                id: editing.id,
                name: trimmed,
                memberIds: memberIds,
                defaultCurrency: currency.rawValue
            )
            await model.updateGroup(updated)
        } else {
            let new = Group(
                name: trimmed,
                memberIds: memberIds,
                defaultCurrency: currency.rawValue
            )
            if let saved = await model.addGroup(new) {
                onCreated?(saved)
            }
        }
        dismiss()
    }

    private func deleteGroup() async {
        guard let editing else { return }
        await model.deleteGroup(id: editing.id)
        dismiss()
    }
}

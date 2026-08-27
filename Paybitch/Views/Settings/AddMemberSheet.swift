//
//  AddMemberSheet.swift
//  Paybitch
//

import SwiftUI

struct AddMemberSheet: View {
    @Environment(ModelData.self) private var model
    @Environment(\.dismiss) private var dismiss

    /// Optional member to edit. When nil, creates new.
    let editing: Member?

    @State private var name: String = ""
    @State private var iconSymbol: String?
    @State private var saving = false

    private let iconColumns = Array(repeating: GridItem(.flexible(), spacing: 10), count: 5)

    /// Live avatar for the preview, reflecting current name + icon choice.
    private var previewMember: Member {
        Member(
            id: editing?.id ?? "preview",
            name: name.isEmpty ? "?" : name,
            imageUrl: editing?.imageUrl,
            iconSymbol: iconSymbol
        )
    }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: 18) {
                    PaybitchAvatar(member: previewMember, size: 88)
                        .padding(.top, 8)

                    PaybitchFieldGroup(label: "Name") {
                        TextField(text: $name, prompt: Text("Type a name").foregroundStyle(Paybitch.textMuted)) {
                            Text("")
                        }
                        .textInputAutocapitalization(.words)
                        .font(.spaceGrotesk(16, weight: .semibold))
                        .foregroundStyle(Paybitch.textPrimary)
                        .padding(.horizontal, 16)
                        .padding(.vertical, 14)
                    }

                    PaybitchFieldGroup(label: "Profile icon") {
                        LazyVGrid(columns: iconColumns, spacing: 10) {
                            iconTile(symbol: nil)
                            ForEach(AvatarIcon.symbols, id: \.self) { sym in
                                iconTile(symbol: sym)
                            }
                        }
                        .padding(12)
                    }
                }
                .padding(.horizontal, 16)
                .padding(.top, 8)
                .padding(.bottom, 60)
            }
            .background(Paybitch.bg.ignoresSafeArea())
            .navigationTitle(editing == nil ? "New member" : "Edit member")
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
            .paybitchAppearance()
            .task {
                if let editing {
                    name = editing.name
                    iconSymbol = editing.iconSymbol
                }
            }
        }
    }

    @ViewBuilder
    private func iconTile(symbol: String?) -> some View {
        let selected = iconSymbol == symbol
        Button {
            iconSymbol = symbol
        } label: {
            SwiftUI.Group {
                if let symbol {
                    Image(systemName: symbol)
                        .font(.system(size: 20, weight: .semibold))
                } else {
                    Text("Aa")
                        .font(.spaceGrotesk(15, weight: .heavy))
                }
            }
            .foregroundStyle(selected ? .white : Paybitch.textPrimary)
            .frame(width: 52, height: 52)
            .background(
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .fill(selected ? Paybitch.pink : Paybitch.chipBg)
            )
            .overlay(
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .strokeBorder(selected ? Color.clear : Paybitch.divider, lineWidth: 1)
            )
        }
        .buttonStyle(.plain)
    }

    private var canSave: Bool {
        !name.trimmingCharacters(in: .whitespaces).isEmpty && !saving
    }

    private func save() async {
        guard !saving else { return }
        saving = true
        defer { saving = false }

        let trimmedName = name.trimmingCharacters(in: .whitespaces)

        if let editing {
            await model.updateMember(
                Member(id: editing.id, name: trimmedName, imageUrl: editing.imageUrl, iconSymbol: iconSymbol)
            )
        } else {
            let new = Member(id: UUID().uuidString, name: trimmedName, imageUrl: nil, iconSymbol: iconSymbol)
            await model.addMember(new)
        }
        dismiss()
    }
}

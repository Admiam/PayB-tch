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
    @State private var imageUrl: String = ""
    @State private var saving = false

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: 18) {
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

                    PaybitchFieldGroup(label: "Avatar URL (optional)") {
                        TextField(text: $imageUrl, prompt: Text("https://…").foregroundStyle(Paybitch.textMuted)) {
                            Text("")
                        }
                        .textInputAutocapitalization(.never)
                        .keyboardType(.URL)
                        .autocorrectionDisabled(true)
                        .font(.spaceGrotesk(15, weight: .semibold))
                        .foregroundStyle(Paybitch.textPrimary)
                        .padding(.horizontal, 16)
                        .padding(.vertical, 14)
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
                ToolbarItem(placement: .confirmationAction) {
                    PaybitchPrimaryButton(title: "Save", disabled: !canSave) {
                        Task { await save() }
                    }
                }
            }
            .task {
                if let editing {
                    name = editing.name
                    imageUrl = editing.imageUrl ?? ""
                }
            }
        }
    }

    private var canSave: Bool {
        !name.trimmingCharacters(in: .whitespaces).isEmpty && !saving
    }

    private func save() async {
        guard !saving else { return }
        saving = true
        defer { saving = false }

        let trimmedName = name.trimmingCharacters(in: .whitespaces)
        let url = imageUrl.trimmingCharacters(in: .whitespaces)
        let imageOpt = url.isEmpty ? nil : url

        if let editing {
            await model.updateMember(Member(id: editing.id, name: trimmedName, imageUrl: imageOpt))
        } else {
            let new = Member(id: UUID().uuidString, name: trimmedName, imageUrl: imageOpt)
            await model.addMember(new)
        }
        dismiss()
    }
}

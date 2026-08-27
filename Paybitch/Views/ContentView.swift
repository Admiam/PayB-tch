//
//  ContentView.swift
//  Paybitch
//

import SwiftUI

struct ContentView: View {
    @Environment(ModelData.self) private var model
    @State private var selectedGroupId: String?
    @State private var path: [Route] = []
    @State private var showAddExpense = false
    @State private var showSettings = false
    @State private var showAddGroup = false
    @State private var editingGroup: Group?
    @State private var showOnboarding = false

    @AppStorage(AppStorageKey.hasOnboarded) private var hasOnboarded: Bool = false

    private var errorBinding: Binding<Bool> {
        Binding(
            get: { model.lastError != nil },
            set: { if !$0 { model.clearError() } }
        )
    }

    enum Route: Hashable {
        case activity(groupId: String)
    }

    private var liveSelectedGroup: Group? {
        guard let id = selectedGroupId else { return nil }
        return model.group(for: id)
    }

    var body: some View {
        NavigationStack(path: $path) {
            ScrollView {
                VStack(spacing: 0) {
                    TopBar(
                        hasSelectedGroup: liveSelectedGroup != nil,
                        onSettings: { showSettings = true },
                        onAddGroup: { showAddGroup = true },
                        onEditGroup: { if let g = liveSelectedGroup { editingGroup = g } },
                        onSearch: { if let g = liveSelectedGroup { path.append(.activity(groupId: g.id)) } }
                    )
                    GroupChipsRow(
                        selectedId: $selectedGroupId,
                        onAddGroup: { showAddGroup = true }
                    )

                    if let group = liveSelectedGroup {
                        groupContent(group: group)
                    } else {
                        emptyGroupsState
                    }
                }
                .padding(.bottom, 100)
            }
            .paybitchBackground()
            .toolbar(.hidden, for: .navigationBar)
            .navigationDestination(for: Route.self) { route in
                switch route {
                case .activity(let groupId):
                    ActivityFeedView(groupId: groupId)
                }
            }
            .overlay(alignment: .bottomTrailing) {
                if liveSelectedGroup != nil {
                    PaybitchFAB { showAddExpense = true }
                        .padding(.trailing, 20)
                        .padding(.bottom, 24)
                }
            }
            .sheet(isPresented: $showAddExpense) {
                if let g = liveSelectedGroup {
                    AddExpenseSheet(group: g).presentationDetents([.large])
                }
            }
            .sheet(isPresented: $showSettings) { SettingsSheet() }
            .sheet(isPresented: $showAddGroup) {
                GroupEditorSheet(editing: nil) { created in
                    selectedGroupId = created.id
                }
            }
            .sheet(item: $editingGroup) { g in
                GroupEditorSheet(editing: g)
            }
            .onChange(of: model.groups.map(\.id)) { _, _ in
                if let id = selectedGroupId, model.group(for: id) == nil {
                    selectedGroupId = nil
                }
                if selectedGroupId == nil, let first = model.groups.first {
                    selectedGroupId = first.id
                }
            }
            .alert("Something went wrong", isPresented: errorBinding, presenting: model.lastError) { _ in
                Button("OK", role: .cancel) {}
            } message: { msg in
                Text(msg)
            }
            .fullScreenCover(isPresented: $showOnboarding) { OnboardingView() }
            .task {
                if selectedGroupId == nil { selectedGroupId = model.groups.first?.id }
                evaluateOnboarding()
            }
            .onChange(of: model.groups.count) { _, _ in evaluateOnboarding() }
        }
    }

    @ViewBuilder
    private func groupContent(group: Group) -> some View {
        let displayCurrency = Currency(rawValue: group.defaultCurrency.uppercased()) ?? .default
        let groupExpenses = model.expenses(forGroup: group.id)
        let balances = BalanceCalculator.balances(
            expenses: groupExpenses,
            memberIds: group.memberIds,
            in: displayCurrency,
            fx: model.fx
        )
        let edges = DebtSimplifier.simplify(balances)
        let myNet: Decimal = balances.first { $0.memberId == model.currentUserId }?.net ?? 0
        let isEmpty = groupExpenses.isEmpty
        let isSettled = !groupExpenses.isEmpty && myNet.magnitude < Decimal(string: "0.01")! && edges.isEmpty

        VStack(spacing: 0) {
            VStack(spacing: 10) {
                Tiles(group: group)
            }
            .padding(.horizontal, 16)
            .padding(.top, 12)

            if isEmpty {
                EmptyExpensesCard { showAddExpense = true }
            } else if isSettled {
                AllSquaredCard()
            }

            if !isEmpty {
                Members(group: group)
                    .padding(.horizontal, 16)
                DebtSummaryList(group: group)
                    .padding(.horizontal, 16)
                DebtsList(group: group)
                    .padding(.horizontal, 16)
            }
        }
    }

    private var emptyGroupsState: some View {
        ContentUnavailableView(
            "No groups yet",
            systemImage: "person.3",
            description: Text("Tap + New to create your first group.")
        )
        .padding(.top, 80)
        .foregroundStyle(Paybitch.textPrimary)
    }

    private func evaluateOnboarding() {
        if !hasOnboarded && model.groups.isEmpty && !showOnboarding {
            showOnboarding = true
        }
    }
}

// MARK: - FAB

struct PaybitchFAB: View {
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: "plus")
                .font(.system(size: 26, weight: .heavy))
                .foregroundStyle(.white)
                .frame(width: 64, height: 64)
        }
        .buttonStyle(PaybitchGlassButton(kind: .accent, shape: Circle()))
        .paybitchShadow(Paybitch.Shadow.pinkFab)
        .accessibilityLabel("Add expense")
    }
}

// MARK: - Empty / settled cards

private struct EmptyExpensesCard: View {
    let onAdd: () -> Void
    var body: some View {
        VStack(spacing: 0) {
            ZStack {
                RoundedRectangle(cornerRadius: 28, style: .continuous)
                    .fill(Paybitch.pink)
                Image(systemName: "wallet.bifold.fill")
                    .font(.system(size: 44, weight: .semibold))
                    .foregroundStyle(.white)
            }
            .frame(width: 88, height: 88)
            .rotationEffect(.degrees(-6))
            .paybitchShadow(Paybitch.Shadow.pinkGlow)

            Text("Nothing on the tab yet")
                .font(.spaceGrotesk(22, weight: .bold))
                .tracking(-0.5)
                .foregroundStyle(Paybitch.textPrimary)
                .padding(.top, 16)

            Text("Drop the first expense and we'll do the math.")
                .font(.spaceGrotesk(14))
                .foregroundStyle(Paybitch.textMuted)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 240)
                .padding(.top, 6)

            PaybitchPrimaryButton(title: "+ Add first expense", action: onAdd)
                .padding(.top, 18)
        }
        .frame(maxWidth: .infinity)
        .padding(28)
        .background(
            RoundedRectangle(cornerRadius: 26, style: .continuous).fill(Paybitch.card)
        )
        .padding(.horizontal, 16)
        .padding(.top, 20)
    }
}

private struct AllSquaredCard: View {
    var body: some View {
        VStack(spacing: 2) {
            Text("All squared up")
                .font(.spaceGrotesk(18, weight: .heavy))
                .tracking(-0.3)
                .foregroundStyle(Paybitch.textPrimary)
            Text("Nobody owes anybody. Nice.")
                .font(.spaceGrotesk(13))
                .foregroundStyle(Paybitch.textMuted)
        }
        .frame(maxWidth: .infinity)
        .padding(18)
        .glassEffect(
            .regular.tint(Paybitch.positive.opacity(0.35)),
            in: RoundedRectangle(cornerRadius: 22, style: .continuous)
        )
        .padding(.horizontal, 16)
        .padding(.top, 14)
    }
}

#Preview {
    ContentView()
        .environment(ModelData())
}

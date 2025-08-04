//
//  ModelData.swift
//  Paybitch
//
//  Created by Adam Míka on 02.08.2025.
//

import Foundation

@MainActor
final class ModelData: ObservableObject {
    
    @Published private(set) var expenses: [Expense] = []
    @Published private(set) var members:  [Member]  = []
    @Published private(set) var groups:   [Group]   = []
    
    init() {
        loadExpenses(named: "expenses")
        loadGroups(named: "groups")
        loadMembers(named: "members")
        
    }
    
    private func loadExpenses(named fileName: String) {
        guard let url = Bundle.main.url(forResource: fileName, withExtension: "json") else {
            fatalError("Couldn't find \(fileName) in main bundle.")
        }
        
        do {
            let data = try Data(contentsOf: url)
            let decoder = JSONDecoder()
            let expenses = try decoder.decode([Expense].self, from: data)
            self.expenses = expenses
        } catch {
            print("Could not load or parse \(fileName).json (\(error))")
            return
        }
        
    }
    
    private func loadGroups(named fileName: String) {
        if let url = Bundle.main.url(forResource: fileName, withExtension: "json") {
            do {
                let data = try Data(contentsOf: url)
                let decoder = JSONDecoder()
                let groups = try decoder.decode([Group].self, from: data)
                self.groups = groups
            } catch {
                print("Could not find \(fileName).json (\(error))")
                return
            }
        }
    }
    
    private func loadMembers(named fileName: String) {
        if let url = Bundle.main.url(forResource: fileName, withExtension: "json") {
            do {
                let data = try Data(contentsOf: url)
                let decoder = JSONDecoder()
                let members = try decoder.decode([Member].self, from: data)
                self.members = members
            } catch {
                print("Could not find \(fileName).json (\(error))")
                return
            }
        }
    }
    
    ///Helpers
    func group(for id: String) -> Group? {
        groups.first { $0.id == id }
    }

    func member(for id: String) -> Member? {
        members.first { $0.id == id }
    }

    func expenses(forGroup id: String) -> [Expense] {
        expenses.filter { $0.groupId == id }
    }

    
}

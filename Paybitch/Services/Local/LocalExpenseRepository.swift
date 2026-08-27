//
//  LocalExpenseRepository.swift
//  Paybitch
//
//  JSON-backed actor. Seeds from bundle on first run, persists to Documents/.
//

import Foundation
import OSLog

private let logger = PaybitchLog.make("LocalExpenseRepo")

actor LocalExpenseRepository: ExpenseRepository {
    private let fileURL: URL
    private let seedResource: String
    private var cache: [Expense] = []
    private var loaded = false

    init(fileName: String = "expenses.json", seedResource: String = "expenses") {
        let docs = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        self.fileURL = docs.appendingPathComponent(fileName)
        self.seedResource = seedResource
    }

    private func loadIfNeeded() {
        guard !loaded else { return }
        loaded = true
        if FileManager.default.fileExists(atPath: fileURL.path) {
            cache = (try? decode(url: fileURL)) ?? []
        } else if let url = Bundle.main.url(forResource: seedResource, withExtension: "json") {
            cache = (try? decode(url: url)) ?? []
            persist()
        }
    }

    private func decode(url: URL) throws -> [Expense] {
        let data = try Data(contentsOf: url)
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try decoder.decode([Expense].self, from: data)
    }

    private func persist() {
        do {
            let encoder = JSONEncoder()
            encoder.dateEncodingStrategy = .iso8601
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            let data = try encoder.encode(cache)
            try data.write(to: fileURL, options: .atomic)
        } catch {
            logger.error("Persist failed: \(error.localizedDescription)")
        }
    }

    func listAll() async throws -> [Expense] {
        loadIfNeeded()
        return cache
    }

    func list(groupId: String) async throws -> [Expense] {
        loadIfNeeded()
        return cache.filter { $0.groupId == groupId }
    }

    func create(_ expense: Expense) async throws -> Expense {
        loadIfNeeded()
        cache.append(expense)
        persist()
        return expense
    }

    func update(_ expense: Expense) async throws -> Expense {
        loadIfNeeded()
        guard let idx = cache.firstIndex(where: { $0.id == expense.id }) else {
            throw RepoError.notFound
        }
        cache[idx] = expense
        persist()
        return expense
    }

    func delete(id: String) async throws {
        loadIfNeeded()
        cache.removeAll { $0.id == id }
        persist()
    }

    enum RepoError: Error { case notFound }
}

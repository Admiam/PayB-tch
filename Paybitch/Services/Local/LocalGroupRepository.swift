//
//  LocalGroupRepository.swift
//  Paybitch
//

import Foundation
import OSLog

private let logger = PaybitchLog.make("LocalGroupRepo")

actor LocalGroupRepository: GroupRepository {
    private let fileURL: URL
    private let seedResource: String
    private var cache: [Group] = []
    private var loaded = false

    init(fileName: String = "groups.json", seedResource: String = "groups") {
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

    private func decode(url: URL) throws -> [Group] {
        let data = try Data(contentsOf: url)
        return try JSONDecoder().decode([Group].self, from: data)
    }

    private func persist() {
        do {
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            let data = try encoder.encode(cache)
            try data.write(to: fileURL, options: .atomic)
        } catch {
            logger.error("Persist failed: \(error.localizedDescription)")
        }
    }

    func list() async throws -> [Group] {
        loadIfNeeded()
        return cache
    }

    func create(_ group: Group) async throws -> Group {
        loadIfNeeded()
        cache.append(group)
        persist()
        return group
    }

    func update(_ group: Group) async throws -> Group {
        loadIfNeeded()
        guard let idx = cache.firstIndex(where: { $0.id == group.id }) else {
            throw RepoError.notFound
        }
        cache[idx] = group
        persist()
        return group
    }

    func delete(id: String) async throws {
        loadIfNeeded()
        cache.removeAll { $0.id == id }
        persist()
    }

    enum RepoError: Error { case notFound }
}

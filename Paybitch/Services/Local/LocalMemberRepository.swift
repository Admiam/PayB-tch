//
//  LocalMemberRepository.swift
//  Paybitch
//

import Foundation
import OSLog

private let logger = PaybitchLog.make("LocalMemberRepo")

actor LocalMemberRepository: MemberRepository {
    private let fileURL: URL
    private let seedResource: String
    private var cache: [Member] = []
    private var loaded = false

    init(fileName: String = "members.json", seedResource: String = "members") {
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

    private func decode(url: URL) throws -> [Member] {
        let data = try Data(contentsOf: url)
        return try JSONDecoder().decode([Member].self, from: data)
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

    func list() async throws -> [Member] {
        loadIfNeeded()
        return cache
    }

    func create(_ member: Member) async throws -> Member {
        loadIfNeeded()
        cache.append(member)
        persist()
        return member
    }

    func update(_ member: Member) async throws -> Member {
        loadIfNeeded()
        guard let idx = cache.firstIndex(where: { $0.id == member.id }) else {
            throw RepoError.notFound
        }
        cache[idx] = member
        persist()
        return member
    }

    func delete(id: String) async throws {
        loadIfNeeded()
        cache.removeAll { $0.id == id }
        persist()
    }

    enum RepoError: Error { case notFound }
}

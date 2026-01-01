import Foundation
import SQLite

public struct BackupFile: Identifiable {
    public let id: String // fileID
    public let domain: String
    public let relativePath: String
    public let flags: Int
    public let fileData: Data?

    public var realPath: String {
        return domain + "/" + relativePath
    }
}

public class StandardBackupReader {
    let backupPath: String
    let db: Connection

    public init(backupPath: String) throws {
        self.backupPath = backupPath
        let manifestPath = URL(fileURLWithPath: backupPath).appendingPathComponent("Manifest.db").path

        guard FileManager.default.fileExists(atPath: manifestPath) else {
            throw NSError(domain: "StandardBackupReader", code: 404, userInfo: [NSLocalizedDescriptionKey: "Manifest.db not found at \(manifestPath)"])
        }

        self.db = try Connection(manifestPath, readonly: true)
    }

    public func listFiles(limit: Int = 1000, search: String? = nil) throws -> [BackupFile] {
        let files = Table("Files")
        let fileID = Expression<String>("fileID")
        let domain = Expression<String>("domain")
        let relativePath = Expression<String>("relativePath")
        let flags = Expression<Int>("flags")
        let file = Expression<Data?>("file")

        var result: [BackupFile] = []

        var query = files

        if let searchTerm = search {
             // Search in both domain and relativePath
             // Note: SQLite.swift uses like() for case-insensitive matching if collation allows,
             // but strictly speaking standard LIKE is case-insensitive for ASCII.
             let likeTerm = "%\(searchTerm)%"
             query = query.filter(relativePath.like(likeTerm) || domain.like(likeTerm))
             // Increase limit significantly if searching, or remove it?
             // Let's keep a higher safety limit for search results.
             query = query.limit(500)
        } else {
             query = query.limit(limit)
        }

        for f in try db.prepare(query) {
            result.append(BackupFile(
                id: f[fileID],
                domain: f[domain],
                relativePath: f[relativePath],
                flags: f[flags],
                fileData: f[file]
            ))
        }
        return result
    }

    public func findFile(domain: String, path: String) throws -> BackupFile? {
        let files = Table("Files")
        let fileID = Expression<String>("fileID")
        let domainCol = Expression<String>("domain")
        let relativePath = Expression<String>("relativePath")
        let flags = Expression<Int>("flags")
        let file = Expression<Data?>("file")

        if let f = try db.pluck(files.filter(domainCol == domain && relativePath == path)) {
            return BackupFile(
                id: f[fileID],
                domain: f[domainCol],
                relativePath: f[relativePath],
                flags: f[flags],
                fileData: f[file]
            )
        }
        return nil
    }

    public func getActualPath(for fileID: String) -> String? {
        // Try two-folder structure first (iOS 10+)
        let prefix = String(fileID.prefix(2))
        let pathWithFolder = URL(fileURLWithPath: backupPath).appendingPathComponent(prefix).appendingPathComponent(fileID).path

        if FileManager.default.fileExists(atPath: pathWithFolder) {
            return pathWithFolder
        }

        // Try root structure (Older iOS)
        let pathRoot = URL(fileURLWithPath: backupPath).appendingPathComponent(fileID).path
        if FileManager.default.fileExists(atPath: pathRoot) {
            return pathRoot
        }

        return nil
    }
}

import Foundation
import Crypto

final class ResumableObjectWriter {
    let rootURL: URL // e.g., snapshotRoot from UIDocumentPicker
    let fileManager = FileManager.default
    let commitLogURL: URL

    init(root: URL) throws {
        self.rootURL = root
        self.commitLogURL = root.appendingPathComponent("commit.log")
        try fileManager.createDirectory(at: root.appendingPathComponent("objects"), withIntermediateDirectories: true)
    }

    func writeChunks(_ chunks: [(String, Data)]) throws {
        // write each chunk as <hash>.chunk.tmp then rename to .chunk
        for (hash, encrypted) in chunks {
            let tmpURL = rootURL.appendingPathComponent("objects/\(hash).chunk.tmp")
            let finalURL = rootURL.appendingPathComponent("objects/\(hash).chunk")
            if fileManager.fileExists(atPath: finalURL.path) {
                // already exist, skip
                continue
            }
            try encrypted.write(to: tmpURL, options: .atomic)
            // fsync - on iOS no direct, but .atomic does rename safely; rename to final
            try fileManager.moveItem(at: tmpURL, to: finalURL)
            // append to commit log
            try appendCommitLine("PUT \(hash)\n")
        }
    }

    func appendCommitLine(_ line: String) throws {
        let data = line.data(using: .utf8)!
        if fileManager.fileExists(atPath: commitLogURL.path) {
            if let handle = try? FileHandle(forWritingTo: commitLogURL) {
                try handle.seekToEnd()
                try handle.write(contentsOf: data)
                try handle.close()
                return
            }
        }
        try data.write(to: commitLogURL, options: .atomic)
    }

    /// Replays commit log to find incomplete or partially written entries.
    func repairIncompleteWrites() {
        guard let data = try? Data(contentsOf: commitLogURL),
              let content = String(data: data, encoding: .utf8) else { return }
        let lines = content.split(separator: "\n")
        for line in lines {
            // simple format: PUT <hash>
            let parts = line.split(separator: " ")
            if parts.count >= 2 && parts[0] == "PUT" {
                let hash = String(parts[1])
                let finalURL = rootURL.appendingPathComponent("objects/\(hash).chunk")
                if !fileManager.fileExists(atPath: finalURL.path) {
                    // mark for re-upload/recreation by upstream logic
                    // for now log
                    print("Missing chunk \(hash) - needs reupload")
                }
            }
        }
    }
}

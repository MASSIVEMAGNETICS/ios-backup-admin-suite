import Foundation
import Crypto

#if canImport(Compression)
import Compression
#endif

/// Restore tool that reads snapshot and reconstructs file tree
public final class BackupRestorer {
    let snapshotURL: URL
    let fileManager = FileManager.default
    let key: SymmetricKey
    
    public init(snapshotURL: URL, encryptionKey: SymmetricKey) {
        self.snapshotURL = snapshotURL
        self.key = encryptionKey
    }
    
    /// Restore a single file from the snapshot
    public func restoreFile(fileEntry: FileEntry, to destinationURL: URL) throws {
        var restoredData = Data()
        
        // Fetch and decrypt each chunk
        for chunkHash in fileEntry.chunkHashes {
            let chunkURL = snapshotURL.appendingPathComponent("objects/\(chunkHash).chunk")
            
            guard fileManager.fileExists(atPath: chunkURL.path) else {
                throw RestoreError.missingChunk(chunkHash)
            }
            
            let encryptedData = try Data(contentsOf: chunkURL)
            
            // Decrypt
            guard let sealedBox = try? AES.GCM.SealedBox(combined: encryptedData) else {
                throw RestoreError.decryptionFailed(chunkHash)
            }
            
            let decryptedCompressed = try AES.GCM.open(sealedBox, using: key)
            
            // Decompress
            guard let decompressed = decompress(data: decryptedCompressed) else {
                throw RestoreError.decompressionFailed(chunkHash)
            }
            
            // Verify chunk hash
            let hash = SHA256.hash(data: decryptedCompressed)
            let hex = hash.map { String(format: "%02x", $0) }.joined()
            guard hex == chunkHash else {
                throw RestoreError.checksumMismatch(expected: chunkHash, got: hex)
            }
            
            restoredData.append(decompressed)
        }
        
        // Verify file hash
        let fileHash = SHA256.hash(data: restoredData)
        let fileHex = fileHash.map { String(format: "%02x", $0) }.joined()
        guard fileHex == fileEntry.fileSHA256 else {
            throw RestoreError.fileChecksumMismatch(expected: fileEntry.fileSHA256, got: fileHex)
        }
        
        // Verify Merkle root
        let computedMerkle = ManifestBuilder.merkleRoot(from: fileEntry.chunkHashes)
        guard computedMerkle == fileEntry.merkleRoot else {
            throw RestoreError.merkleRootMismatch(expected: fileEntry.merkleRoot, got: computedMerkle)
        }
        
        // Write restored file
        try restoredData.write(to: destinationURL, options: .atomic)
        
        print("✓ Restored: \(fileEntry.path) (\(restoredData.count) bytes)")
    }
    
    /// Restore all files from a snapshot
    public func restoreAll(to destinationRoot: URL) throws {
        let filesDir = snapshotURL.appendingPathComponent("files")
        
        guard fileManager.fileExists(atPath: filesDir.path) else {
            throw RestoreError.filesDirectoryNotFound
        }
        
        let fileURLs = try fileManager.contentsOfDirectory(at: filesDir, includingPropertiesForKeys: nil)
        
        for fileURL in fileURLs where fileURL.pathExtension == "json" {
            let fileEntryData = try Data(contentsOf: fileURL)
            let fileEntry = try JSONDecoder().decode(FileEntry.self, from: fileEntryData)
            
            let destURL = destinationRoot.appendingPathComponent(fileEntry.path)
            
            // Create parent directory if needed
            let parentURL = destURL.deletingLastPathComponent()
            try fileManager.createDirectory(at: parentURL, withIntermediateDirectories: true)
            
            try restoreFile(fileEntry: fileEntry, to: destURL)
        }
        
        print("\n✓ Restore completed successfully!")
    }
    
    private func decompress(data: Data) -> Data? {
        #if canImport(Compression)
        let dstSize = data.count * 4 // estimate 4x expansion
        var dst = Data(count: dstSize)
        let result = dst.withUnsafeMutableBytes { dstPtr -> Int in
            return data.withUnsafeBytes { srcPtr in
                compression_decode_buffer(
                    dstPtr.bindMemory(to: UInt8.self).baseAddress!,
                    dstSize,
                    srcPtr.bindMemory(to: UInt8.self).baseAddress!,
                    data.count,
                    nil,
                    COMPRESSION_LZFSE
                )
            }
        }
        if result == 0 { return nil }
        dst.count = result
        return dst
        #else
        // Fallback: no decompression on non-Apple platforms
        // Data was not compressed, so return as-is
        return data
        #endif
    }
}

enum RestoreError: Error, CustomStringConvertible {
    case missingChunk(String)
    case decryptionFailed(String)
    case decompressionFailed(String)
    case checksumMismatch(expected: String, got: String)
    case fileChecksumMismatch(expected: String, got: String)
    case merkleRootMismatch(expected: String, got: String)
    case filesDirectoryNotFound
    
    var description: String {
        switch self {
        case .missingChunk(let hash):
            return "Missing chunk: \(hash)"
        case .decryptionFailed(let hash):
            return "Decryption failed for chunk: \(hash)"
        case .decompressionFailed(let hash):
            return "Decompression failed for chunk: \(hash)"
        case .checksumMismatch(let expected, let got):
            return "Chunk checksum mismatch - expected: \(expected), got: \(got)"
        case .fileChecksumMismatch(let expected, let got):
            return "File checksum mismatch - expected: \(expected), got: \(got)"
        case .merkleRootMismatch(let expected, let got):
            return "Merkle root mismatch - expected: \(expected), got: \(got)"
        case .filesDirectoryNotFound:
            return "Files directory not found in snapshot"
        }
    }
}

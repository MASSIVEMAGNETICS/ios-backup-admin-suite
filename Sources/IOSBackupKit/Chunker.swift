import Foundation
import Crypto

#if canImport(Compression)
import Compression
#endif

public enum StorageError: Error {
    case compressionFailed
    case encryptionFailed
}

public final class Chunker {
    let chunkSize: Int
    let key: SymmetricKey  // AES key from CryptoKit

    public init(chunkSize: Int = 64 * 1024, encryptionKey: SymmetricKey) {
        self.chunkSize = chunkSize
        self.key = encryptionKey
    }

    /// Returns array of (sha256Hex, encryptedData)
    public func chunkAndEncrypt(data: Data) throws -> [(String, Data)] {
        var results: [(String, Data)] = []
        var offset = 0
        while offset < data.count {
            let end = Swift.min(offset + chunkSize, data.count)
            let slice = data.subdata(in: offset..<end)
            // compress
            guard let comp = compress(data: slice) else { throw StorageError.compressionFailed }
            // hash BEFORE encryption
            let hash = SHA256.hash(data: comp)
            let hex = hash.map { String(format: "%02x", $0) }.joined()
            // encrypt
            let sealed = try AES.GCM.seal(comp, using: key)
            guard let combined = sealed.combined else { throw StorageError.encryptionFailed }
            results.append((hex, combined))
            offset = end
        }
        return results
    }

    private func compress(data: Data) -> Data? {
        #if canImport(Compression)
        let dstSize = max(4096, data.count)
        var dst = Data(count: dstSize)
        let result = dst.withUnsafeMutableBytes { dstPtr -> Int in
            return data.withUnsafeBytes { srcPtr in
                compression_encode_buffer(
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
        // Fallback: no compression on non-Apple platforms
        // In production, use a cross-platform compression library like swift-nio-compress
        return data
        #endif
    }
}

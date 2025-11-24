import XCTest
@testable import IOSBackupKit
import Crypto

final class IOSBackupKitTests: XCTestCase {
    
    // MARK: - Chunker Tests
    
    func testChunkerWithSmallData() throws {
        let key = SymmetricKey(size: .bits256)
        let chunker = Chunker(chunkSize: 1024, encryptionKey: key)
        
        let testData = Data(repeating: 0x42, count: 500)
        let chunks = try chunker.chunkAndEncrypt(data: testData)
        
        XCTAssertEqual(chunks.count, 1, "Small data should produce one chunk")
        
        let (hash, encrypted) = chunks[0]
        XCTAssertEqual(hash.count, 64, "SHA-256 hash should be 64 hex characters")
        XCTAssertGreaterThan(encrypted.count, 0, "Encrypted data should not be empty")
    }
    
    func testChunkerWithMultipleChunks() throws {
        let key = SymmetricKey(size: .bits256)
        let chunker = Chunker(chunkSize: 1024, encryptionKey: key)
        
        let testData = Data(repeating: 0x42, count: 5000)
        let chunks = try chunker.chunkAndEncrypt(data: testData)
        
        XCTAssertEqual(chunks.count, 5, "5000 bytes should produce 5 chunks with 1024 chunk size")
        
        // Verify all chunks have valid hashes
        for (hash, encrypted) in chunks {
            XCTAssertEqual(hash.count, 64)
            XCTAssertGreaterThan(encrypted.count, 0)
        }
    }
    
    func testChunkerEmptyData() throws {
        let key = SymmetricKey(size: .bits256)
        let chunker = Chunker(chunkSize: 1024, encryptionKey: key)
        
        let testData = Data()
        let chunks = try chunker.chunkAndEncrypt(data: testData)
        
        XCTAssertEqual(chunks.count, 0, "Empty data should produce no chunks")
    }
    
    // MARK: - Manifest Builder Tests
    
    func testMerkleRootSingleHash() {
        let hashes = ["a" + String(repeating: "0", count: 63)]
        let root = ManifestBuilder.merkleRoot(from: hashes)
        
        XCTAssertEqual(root.count, 64, "Merkle root should be a SHA-256 hash (64 hex chars)")
    }
    
    func testMerkleRootMultipleHashes() {
        let hashes = [
            "a" + String(repeating: "0", count: 63),
            "b" + String(repeating: "0", count: 63),
            "c" + String(repeating: "0", count: 63),
            "d" + String(repeating: "0", count: 63)
        ]
        let root = ManifestBuilder.merkleRoot(from: hashes)
        
        XCTAssertEqual(root.count, 64, "Merkle root should be a SHA-256 hash")
    }
    
    func testMerkleRootEmptyList() {
        let hashes: [String] = []
        let root = ManifestBuilder.merkleRoot(from: hashes)
        
        XCTAssertEqual(root.count, 64, "Empty hash list should produce a hash")
    }
    
    func testMerkleRootDeterministic() {
        let hashes = [
            "a" + String(repeating: "0", count: 63),
            "b" + String(repeating: "0", count: 63)
        ]
        
        let root1 = ManifestBuilder.merkleRoot(from: hashes)
        let root2 = ManifestBuilder.merkleRoot(from: hashes)
        
        XCTAssertEqual(root1, root2, "Merkle root should be deterministic")
    }
    
    // MARK: - Data Extension Tests
    
    func testDataHexConversion() {
        let data = Data([0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef])
        let hex = data.hex
        
        XCTAssertEqual(hex, "0123456789abcdef")
    }
    
    func testDataFromHex() {
        let hex = "0123456789abcdef"
        let data = Data(hex: hex)
        
        XCTAssertEqual(data.count, 8)
        XCTAssertEqual(data[0], 0x01)
        XCTAssertEqual(data[1], 0x23)
        XCTAssertEqual(data[7], 0xef)
    }
    
    func testDataHexRoundTrip() {
        let original = Data([0x12, 0x34, 0x56, 0x78, 0x9a, 0xbc, 0xde, 0xf0])
        let hex = original.hex
        let restored = Data(hex: hex)
        
        XCTAssertEqual(original, restored, "Hex conversion should be reversible")
    }
    
    // MARK: - File Entry Tests
    
    func testFileEntryCodable() throws {
        let metadata: [String: AnyCodable] = [
            "creationDate": AnyCodable("2025-11-23T10:00:00Z"),
            "size": AnyCodable(1024)
        ]
        
        let fileEntry = FileEntry(
            path: "test/photo.jpg",
            size: 1024,
            chunkHashes: ["abc123", "def456"],
            fileSHA256: "fedcba",
            metadata: metadata,
            merkleRoot: "root123"
        )
        
        let encoder = JSONEncoder()
        encoder.outputFormatting = .prettyPrinted
        let jsonData = try encoder.encode(fileEntry)
        
        let decoder = JSONDecoder()
        let decoded = try decoder.decode(FileEntry.self, from: jsonData)
        
        XCTAssertEqual(decoded.path, fileEntry.path)
        XCTAssertEqual(decoded.size, fileEntry.size)
        XCTAssertEqual(decoded.chunkHashes, fileEntry.chunkHashes)
        XCTAssertEqual(decoded.fileSHA256, fileEntry.fileSHA256)
        XCTAssertEqual(decoded.merkleRoot, fileEntry.merkleRoot)
    }
    
    // MARK: - Integration Tests
    
    func testChunkAndRestore() throws {
        let key = SymmetricKey(size: .bits256)
        let chunker = Chunker(chunkSize: 1024, encryptionKey: key)
        
        // Original data
        let originalData = Data("Hello, World! This is a test message for chunking and restoration.".utf8)
        
        // Chunk and encrypt
        let chunks = try chunker.chunkAndEncrypt(data: originalData)
        
        XCTAssertGreaterThan(chunks.count, 0, "Should produce at least one chunk")
        
        // Simulate decryption and decompression (basic test)
        for (hash, encrypted) in chunks {
            // Verify we can decrypt
            let sealedBox = try AES.GCM.SealedBox(combined: encrypted)
            let decrypted = try AES.GCM.open(sealedBox, using: key)
            
            XCTAssertGreaterThan(decrypted.count, 0, "Decrypted data should not be empty")
        }
    }
}

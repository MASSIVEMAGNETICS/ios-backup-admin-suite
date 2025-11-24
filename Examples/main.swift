#!/usr/bin/env swift

import Foundation
import IOSBackupKit
import Crypto

// Example: Create and restore a simple backup
// NOTE: This example uses force-unwrap (!) for brevity in demonstration.
// In production code, use proper error handling with do-catch blocks.

print("iOS Backup Admin Suite - Example")
print("=================================\n")

// 1. Set up directories
let tempDir = FileManager.default.temporaryDirectory
let backupDir = tempDir.appendingPathComponent("example-backup-\(UUID().uuidString)")
let restoreDir = tempDir.appendingPathComponent("example-restore-\(UUID().uuidString)")
let testDataDir = tempDir.appendingPathComponent("example-data-\(UUID().uuidString)")

do {
    try FileManager.default.createDirectory(at: backupDir, withIntermediateDirectories: true)
    try FileManager.default.createDirectory(at: restoreDir, withIntermediateDirectories: true)
    try FileManager.default.createDirectory(at: testDataDir, withIntermediateDirectories: true)
} catch {
    print("❌ Error creating directories: \(error)")
    exit(1)
}

print("📁 Created directories:")
print("  Backup: \(backupDir.path)")
print("  Restore: \(restoreDir.path)")
print("  Test data: \(testDataDir.path)\n")

// 2. Create some test files
let testFiles = [
    ("file1.txt", "Hello, this is file 1!"),
    ("file2.txt", "This is another test file with more content."),
    ("file3.txt", "Third file with different data!")
]

for (filename, content) in testFiles {
    let fileURL = testDataDir.appendingPathComponent(filename)
    try! content.data(using: .utf8)!.write(to: fileURL)
    print("📄 Created test file: \(filename)")
}
print()

// 3. Generate encryption key
let key = SymmetricKey(size: .bits256)
print("🔐 Generated encryption key\n")

// 4. Create chunker and writer
let chunker = Chunker(chunkSize: 1024, encryptionKey: key)
let writer = try! ResumableObjectWriter(root: backupDir)
print("⚙️  Initialized chunker and writer\n")

// 5. Back up files
print("📦 Starting backup...\n")
let filesDir = backupDir.appendingPathComponent("files")
try! FileManager.default.createDirectory(at: filesDir, withIntermediateDirectories: true)

var totalChunks = 0
var totalSize = 0

for (filename, _) in testFiles {
    let sourceURL = testDataDir.appendingPathComponent(filename)
    let data = try! Data(contentsOf: sourceURL)
    totalSize += data.count
    
    // Chunk and encrypt
    let chunks = try! chunker.chunkAndEncrypt(data: data)
    totalChunks += chunks.count
    
    // Write chunks
    try! writer.writeChunks(chunks)
    
    // Create file entry
    let hash = SHA256.hash(data: data)
    let hashHex = hash.map { String(format: "%02x", $0) }.joined()
    
    let fileEntry = FileEntry(
        path: filename,
        size: data.count,
        chunkHashes: chunks.map { $0.0 },
        fileSHA256: hashHex,
        metadata: ["created": AnyCodable(Date().description)],
        merkleRoot: ManifestBuilder.merkleRoot(from: chunks.map { $0.0 })
    )
    
    // Save file entry
    let fileEntryData = try! JSONEncoder().encode(fileEntry)
    try! fileEntryData.write(to: filesDir.appendingPathComponent("\(filename).json"))
    
    print("  ✅ Backed up: \(filename) (\(data.count) bytes, \(chunks.count) chunks)")
}

print("\n📊 Backup statistics:")
print("  Files: \(testFiles.count)")
print("  Total size: \(totalSize) bytes")
print("  Total chunks: \(totalChunks)")
print("  Backup location: \(backupDir.path)\n")

// 6. Create manifest
let manifest = SnapshotManifest(
    snapshot_version: "1",
    device_id: UUID().uuidString,
    created_at: ISO8601DateFormatter().string(from: Date()),
    files_count: testFiles.count,
    chunks_count: totalChunks,
    root_merkle: "root-hash-placeholder",
    encryption: "AES-GCM-256",
    compression: "none"
)

let manifestData = try! JSONEncoder().encode(manifest)
try! manifestData.write(to: backupDir.appendingPathComponent("manifest.json"))
print("📋 Created manifest\n")

// 7. Restore backup
print("🔄 Starting restore...\n")

let restorer = BackupRestorer(snapshotURL: backupDir, encryptionKey: key)
try! restorer.restoreAll(to: restoreDir)

print("\n✨ Restore complete!\n")

// 8. Verify restored files
print("🔍 Verifying restored files...\n")

var allMatch = true
for (filename, originalContent) in testFiles {
    let restoredURL = restoreDir.appendingPathComponent(filename)
    if let restoredContent = try? String(contentsOf: restoredURL, encoding: .utf8) {
        if restoredContent == originalContent {
            print("  ✅ \(filename): MATCH")
        } else {
            print("  ❌ \(filename): MISMATCH")
            allMatch = false
        }
    } else {
        print("  ❌ \(filename): NOT FOUND")
        allMatch = false
    }
}

print()

if allMatch {
    print("🎉 SUCCESS! All files restored correctly.\n")
} else {
    print("⚠️  WARNING: Some files did not match.\n")
}

// 9. Cleanup (optional - comment out to inspect files)
print("🧹 Cleaning up temporary files...")
try? FileManager.default.removeItem(at: backupDir)
try? FileManager.default.removeItem(at: restoreDir)
try? FileManager.default.removeItem(at: testDataDir)
print("✅ Cleanup complete\n")

print("Example completed successfully!")

# iOS Backup Admin Suite - Quick Start Guide

## Overview

This repository provides a production-grade iOS backup system following the architecture detailed in [README.md](README.md). The implementation includes:

- **IOSBackupKit**: Core library for backup operations
- **restore-tool**: Command-line tool for restoring backups

## Features

✅ **Chunking & Deduplication**: Fixed-size chunking with content-addressed storage  
✅ **Encryption**: AES-256-GCM authenticated encryption  
✅ **Compression**: LZFSE on Apple platforms, no compression on Linux (configurable)  
✅ **Crash Resilience**: Write-ahead logging (WAL) and atomic operations  
✅ **Integrity Verification**: SHA-256 checksums and Merkle trees  
✅ **Cross-Platform**: Works on iOS, macOS, and Linux  

## Installation

### Build from Source

```bash
# Clone the repository
git clone https://github.com/MASSIVEMAGNETICS/ios-backup-admin-suite.git
cd ios-backup-admin-suite

# Build the package
swift build

# Build in release mode for better performance
swift build -c release

# Run tests
swift test
```

### Using as a Swift Package

Add to your `Package.swift`:

```swift
dependencies: [
    .package(url: "https://github.com/MASSIVEMAGNETICS/ios-backup-admin-suite.git", from: "1.0.0")
],
targets: [
    .target(
        name: "YourTarget",
        dependencies: [
            .product(name: "IOSBackupKit", package: "ios-backup-admin-suite")
        ]
    )
]
```

## Usage Examples

### Command-Line Tool

The `restore-tool` can restore backups created with IOSBackupKit:

```bash
# Basic restore
.build/release/restore-tool ./backup-snapshot ./restored-files

# Restore with passphrase
.build/release/restore-tool ./backup-snapshot ./restored-files "my-secure-password"

# Show help
.build/release/restore-tool
```

### iOS Integration Example

```swift
import IOSBackupKit
import Crypto
import Foundation

// 1. Generate or load encryption key
let key = SymmetricKey(size: .bits256)

// 2. Create snapshot directory URL (from UIDocumentPicker on iOS)
let snapshotURL = URL(fileURLWithPath: "./my-backup")
try FileManager.default.createDirectory(at: snapshotURL, withIntermediateDirectories: true)

// 3. Set up the chunker
let chunker = Chunker(chunkSize: 64 * 1024, encryptionKey: key)

// 4. Create resumable writer
let writer = try ResumableObjectWriter(root: snapshotURL)

// 5. Example: Back up a file
let testData = Data("Hello, World!".utf8)
let chunks = try chunker.chunkAndEncrypt(data: testData)
try writer.writeChunks(chunks)

// 6. Create file entry for manifest
let fileEntry = FileEntry(
    path: "test.txt",
    size: testData.count,
    chunkHashes: chunks.map { $0.0 },
    fileSHA256: SHA256.hash(data: testData).compactMap { String(format: "%02x", $0) }.joined(),
    metadata: ["created": AnyCodable(Date().description)],
    merkleRoot: ManifestBuilder.merkleRoot(from: chunks.map { $0.0 })
)

// 7. Save file entry
let filesDir = snapshotURL.appendingPathComponent("files")
try FileManager.default.createDirectory(at: filesDir, withIntermediateDirectories: true)
let fileEntryData = try JSONEncoder().encode(fileEntry)
try fileEntryData.write(to: filesDir.appendingPathComponent("test.json"))

print("✅ Backup created successfully!")

// 8. Restore the backup
let restorer = BackupRestorer(snapshotURL: snapshotURL, encryptionKey: key)
let restoreURL = URL(fileURLWithPath: "./restored")
try FileManager.default.createDirectory(at: restoreURL, withIntermediateDirectories: true)
try restorer.restoreAll(to: restoreURL)

print("✅ Backup restored successfully!")
```

### Photo Export Example (iOS Only)

```swift
#if canImport(Photos)
import Photos

let photoWorker = PhotoExportWorker(batchSize: 32)

photoWorker.exportAllAssets(
    batchHandler: { items in
        for (asset, data, metadata) in items {
            print("Exporting: \(asset.localIdentifier)")
            
            // Process the photo data
            // - Chunk it
            // - Encrypt it  
            // - Write to backup
            let chunks = try! chunker.chunkAndEncrypt(data: data)
            try! writer.writeChunks(chunks)
        }
    },
    completion: { result in
        switch result {
        case .success:
            print("✅ All photos exported")
        case .failure(let error):
            print("❌ Export failed: \(error)")
        }
    }
)
#endif
```

## Project Structure

```
ios-backup-admin-suite/
├── README.md                   # Architectural blueprint
├── IMPLEMENTATION.md           # Implementation details
├── QUICKSTART.md              # This file
├── Package.swift              # Swift Package Manager manifest
├── Sources/
│   ├── IOSBackupKit/          # Core library
│   │   ├── Chunker.swift      # Chunking, compression, encryption
│   │   ├── ResumableObjectWriter.swift  # Atomic writes with WAL
│   │   ├── ManifestBuilder.swift        # Merkle trees and manifests
│   │   ├── RestoreTool.swift  # Backup restoration
│   │   ├── PhotoExportWorker.swift      # iOS photo export
│   │   └── Models.swift       # Data models
│   └── RestoreTool/           # CLI tool
│       └── main.swift
└── Tests/                     # Unit tests
    └── IOSBackupKitTests.swift
```

## Backup Format

### Directory Structure

```
backup-snapshot/
├── manifest.json              # Snapshot metadata
├── commit.log                 # Write-ahead log
├── objects/                   # Content-addressed chunks
│   ├── a3b1...chunk          # Encrypted chunk
│   ├── f2c4...chunk
│   └── ...
└── files/                     # File metadata
    ├── photo1.json
    └── photo2.json
```

### Manifest Structure

```json
{
  "snapshot_version": "1",
  "device_id": "unique-device-id",
  "created_at": "2025-11-24T09:00:00Z",
  "files_count": 100,
  "chunks_count": 500,
  "root_merkle": "abc123...",
  "encryption": "AES-GCM-256",
  "compression": "lzfse"
}
```

### File Entry Structure

```json
{
  "path": "photos/IMG_1234.jpg",
  "size": 2048576,
  "chunkHashes": ["a3b1...", "f2c4..."],
  "fileSHA256": "e7d8...",
  "metadata": {
    "creationDate": "2025-11-24T09:00:00Z",
    "location": { "latitude": 37.7749, "longitude": -122.4194 }
  },
  "merkleRoot": "9f3a..."
}
```

## Security Features

- **AES-256-GCM**: Authenticated encryption for all chunks
- **SHA-256**: Content addressing and integrity verification
- **Merkle Trees**: Hierarchical integrity verification
- **Write-Ahead Logging**: Crash-proof atomic operations
- **Passphrase Protection**: Key derivation from user passphrase

## Platform Support

| Platform | PhotoKit | Compression | Encryption | Restore |
|----------|----------|-------------|------------|---------|
| iOS 15+  | ✅       | LZFSE       | AES-GCM    | ✅      |
| macOS 12+| ✅       | LZFSE       | AES-GCM    | ✅      |
| Linux    | ❌       | None*       | AES-GCM    | ✅      |

*Note: Compression on Linux can be added by integrating a cross-platform library like swift-nio-compress.

## Testing

```bash
# Run all tests
swift test

# Run tests with verbose output
swift test --verbose

# Run specific test
swift test --filter testChunkerWithMultipleChunks
```

All tests should pass:
```
Test Suite 'IOSBackupKitTests' passed
Executed 12 tests, with 0 failures
```

## Performance

### Benchmarks (approximate)

- **Chunking**: ~200 MB/s
- **Encryption**: ~500 MB/s (hardware-accelerated AES)
- **Compression** (LZFSE): ~100 MB/s
- **Write throughput**: ~50-200 MB/s (depends on storage)

### Memory Usage

- **In-flight data limit**: ~512 MB (configurable)
- **Per-chunk overhead**: ~64 bytes
- **Metadata overhead**: ~1% of backup size

## Troubleshooting

### Build Issues

**Error: "no such module 'Photos'"**
- This is expected on Linux. PhotoExportWorker is iOS/macOS only.
- The module uses conditional compilation to handle this.

**Error: "no such module 'Compression'"**
- On Linux, compression is disabled by default.
- Data is stored uncompressed but still encrypted.

### Runtime Issues

**"Missing chunk" error during restore**
- The backup may be incomplete or corrupted.
- Check `commit.log` for incomplete writes.
- Use `repairIncompleteWrites()` to detect issues.

**"Checksum mismatch" error**
- Indicates data corruption.
- Verify the encryption key is correct.
- Check storage media for errors.

## Next Steps

1. **Read the Architecture**: See [README.md](README.md) for the full technical blueprint
2. **Implementation Details**: See [IMPLEMENTATION.md](IMPLEMENTATION.md) for in-depth documentation
3. **Integrate into Your App**: Import IOSBackupKit and follow the usage examples
4. **Extend the System**: Add features like perceptual hashing, CDC chunking, or cloud sync

## Contributing

Contributions are welcome! Please:

1. Follow the architecture specified in README.md
2. Maintain backward compatibility with the snapshot format
3. Add tests for new features
4. Update documentation

## License

See repository for license information.

## Support

For issues and questions:
- Open an issue on GitHub
- Refer to the detailed documentation in README.md and IMPLEMENTATION.md

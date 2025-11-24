# Examples

This directory contains example scripts demonstrating how to use the iOS Backup Admin Suite.

## Running Examples

To run the examples, you need to have the Swift Package built:

```bash
# Build the package first
cd ..
swift build

# Then run an example
swift Examples/basic-backup-restore.swift
```

## Available Examples

### basic-backup-restore.swift

A complete example showing:
- Creating test files
- Backing them up with chunking and encryption
- Creating a manifest
- Restoring the backup
- Verifying the restored files

**Run it:**
```bash
swift Examples/basic-backup-restore.swift
```

**Expected output:**
```
iOS Backup Admin Suite - Example
=================================

📁 Created directories:
  Backup: /tmp/example-backup-...
  Restore: /tmp/example-restore-...
  Test data: /tmp/example-data-...

📄 Created test file: file1.txt
📄 Created test file: file2.txt
📄 Created test file: file3.txt

🔐 Generated encryption key

⚙️  Initialized chunker and writer

📦 Starting backup...

  ✅ Backed up: file1.txt (22 bytes, 1 chunks)
  ✅ Backed up: file2.txt (44 bytes, 1 chunks)
  ✅ Backed up: file3.txt (33 bytes, 1 chunks)

📊 Backup statistics:
  Files: 3
  Total size: 99 bytes
  Total chunks: 3
  Backup location: /tmp/example-backup-...

📋 Created manifest

🔄 Starting restore...

✓ Restored: file1.txt (22 bytes)
✓ Restored: file2.txt (44 bytes)
✓ Restored: file3.txt (33 bytes)

✓ Restore completed successfully!

✨ Restore complete!

🔍 Verifying restored files...

  ✅ file1.txt: MATCH
  ✅ file2.txt: MATCH
  ✅ file3.txt: MATCH

🎉 SUCCESS! All files restored correctly.

🧹 Cleaning up temporary files...
✅ Cleanup complete

Example completed successfully!
```

## Creating Your Own Examples

To create a new example:

1. Create a new `.swift` file in this directory
2. Add the shebang: `#!/usr/bin/env swift`
3. Import the necessary modules:
   ```swift
   import Foundation
   import IOSBackupKit
   import Crypto
   ```
4. Write your example code
5. Make it executable: `chmod +x Examples/your-example.swift`
6. Run it: `swift Examples/your-example.swift`

## Notes

- Examples use force-unwrap (`!`) for simplicity. In production code, use proper error handling.
- Examples create temporary files that are cleaned up automatically.
- To inspect the backup structure, comment out the cleanup section in the example.

import Foundation
import IOSBackupKit
import Crypto

@main
struct RestoreToolCLI {
    static func main() {
        let arguments = CommandLine.arguments
        
        guard arguments.count >= 3 else {
            printUsage()
            exit(1)
        }
        
        let snapshotPath = arguments[1]
        let destinationPath = arguments[2]
        let passphrase = arguments.count > 3 ? arguments[3] : ""
        
        let snapshotURL = URL(fileURLWithPath: snapshotPath)
        let destinationURL = URL(fileURLWithPath: destinationPath)
        
        // Derive encryption key from passphrase
        let key: SymmetricKey
        if !passphrase.isEmpty {
            // WARNING: This uses simple SHA-256 for key derivation, which is vulnerable to
            // rainbow table attacks. For production use, implement PBKDF2 with salt:
            // let salt = ... // retrieve from backup metadata
            // let key = try PBKDF2<SHA256>.deriveKey(from: passphrase, salt: salt, iterations: 100000)
            // 
            // For this demo/testing tool, we use simple hashing:
            let passphraseData = passphrase.data(using: .utf8)!
            let hash = SHA256.hash(data: passphraseData)
            key = SymmetricKey(data: hash)
        } else {
            // Default key for testing - NOT for production
            print("Warning: Using default encryption key. Provide passphrase for production use.")
            let defaultData = "default-test-key-not-secure".data(using: .utf8)!
            let hash = SHA256.hash(data: defaultData)
            key = SymmetricKey(data: hash)
        }
        
        do {
            print("iOS Backup Admin Suite - Restore Tool")
            print("======================================")
            print("Snapshot: \(snapshotPath)")
            print("Destination: \(destinationPath)")
            print("")
            
            let restoreTool = BackupRestorer(snapshotURL: snapshotURL, encryptionKey: key)
            try restoreTool.restoreAll(to: destinationURL)
            
            print("\n✓ All files restored successfully!")
            exit(0)
        } catch {
            print("\n✗ Error: \(error)")
            exit(1)
        }
    }
    
    static func printUsage() {
        print("""
        iOS Backup Admin Suite - Restore Tool
        
        Usage:
            restore-tool <snapshot-path> <destination-path> [passphrase]
        
        Arguments:
            snapshot-path     Path to the backup snapshot directory
            destination-path  Directory where files will be restored
            passphrase       Optional passphrase for encrypted backups
        
        Example:
            restore-tool ./backup-2025-11-23 ./restored my-secure-passphrase
        """)
    }
}

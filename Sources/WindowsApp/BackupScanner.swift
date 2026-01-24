import Foundation

struct BackupInfo {
    let path: String
    let name: String
    let type: String // "Standard" or "Custom"
    let date: Date?
}

class BackupScanner {
    static func scanForBackups() {
        print("\n🔍 Automatic Scan: Checking for existing iOS backups...")

        var foundBackups: [BackupInfo] = []

        // 1. Scan Standard iTunes/MobileSync Locations
        let standardPaths = getStandardBackupPaths()
        for path in standardPaths {
            if let backups = scanStandardLocation(path: path) {
                foundBackups.append(contentsOf: backups)
            }
        }

        // 2. Scan Current Directory (for custom backups created by this app)
        let currentDir = FileManager.default.currentDirectoryPath
        if let customBackups = scanForCustomBackups(in: currentDir) {
            foundBackups.append(contentsOf: customBackups)
        }

        // Report results
        if foundBackups.isEmpty {
            print("   No existing backups found in standard locations.")
        } else {
            print("   Found \(foundBackups.count) existing backup(s):")
            for backup in foundBackups {
                let dateStr: String
                if let date = backup.date {
                    let formatter = DateFormatter()
                    formatter.dateStyle = .medium
                    formatter.timeStyle = .short
                    dateStr = formatter.string(from: date)
                } else {
                    dateStr = "Unknown Date"
                }
                print("   - [\(backup.type)] \(backup.name) (\(dateStr))")
                print("     Path: \(backup.path)")
            }
        }
        print("") // Empty line for spacing
    }

    private static func getStandardBackupPaths() -> [String] {
        var paths: [String] = []
        let fileManager = FileManager.default

        #if os(Windows)
        // Windows typical locations
        // %APPDATA%\Apple Computer\MobileSync\Backup
        if let appData = ProcessInfo.processInfo.environment["APPDATA"] {
            paths.append(appData + "\\Apple Computer\\MobileSync\\Backup")
        }
        // %USERPROFILE%\Apple\MobileSync\Backup (Store version)
        if let userProfile = ProcessInfo.processInfo.environment["USERPROFILE"] {
            paths.append(userProfile + "\\Apple\\MobileSync\\Backup")
        }
        // Also check typical explicit paths if env vars missing
        paths.append("C:\\Users\\Default\\AppData\\Roaming\\Apple Computer\\MobileSync\\Backup")
        #else
        // macOS / Linux (fallback)
        // ~/Library/Application Support/MobileSync/Backup
        let home = fileManager.homeDirectoryForCurrentUser.path
        paths.append(home + "/Library/Application Support/MobileSync/Backup")
        #endif

        return paths
    }

    private static func scanStandardLocation(path: String) -> [BackupInfo]? {
        let fileManager = FileManager.default
        var results: [BackupInfo] = []

        var isDir: ObjCBool = false
        guard fileManager.fileExists(atPath: path, isDirectory: &isDir), isDir.boolValue else {
            return nil
        }

        do {
            let items = try fileManager.contentsOfDirectory(atPath: path)
            for item in items {
                let itemPath = (path as NSString).appendingPathComponent(item)
                var itemIsDir: ObjCBool = false
                if fileManager.fileExists(atPath: itemPath, isDirectory: &itemIsDir) && itemIsDir.boolValue {
                    // Check if it looks like a backup (Manifest.db or Manifest.plist or Status.plist)
                    if fileManager.fileExists(atPath: (itemPath as NSString).appendingPathComponent("Manifest.plist")) ||
                       fileManager.fileExists(atPath: (itemPath as NSString).appendingPathComponent("Manifest.db")) ||
                       fileManager.fileExists(atPath: (itemPath as NSString).appendingPathComponent("Status.plist")) {

                        let attr = try? fileManager.attributesOfItem(atPath: itemPath)
                        let date = attr?[.modificationDate] as? Date

                        results.append(BackupInfo(
                            path: itemPath,
                            name: item, // Directory name is usually the UDID
                            type: "iTunes/Standard",
                            date: date
                        ))
                    }
                }
            }
        } catch {
            // Ignore errors reading directory
            return nil
        }

        return results
    }

    private static func scanForCustomBackups(in directory: String) -> [BackupInfo]? {
        // Look for manifest.json in subdirectories
        let fileManager = FileManager.default
        var results: [BackupInfo] = []

        do {
            let items = try fileManager.contentsOfDirectory(atPath: directory)
            for item in items {
                let itemPath = (directory as NSString).appendingPathComponent(item)
                var isDir: ObjCBool = false
                if fileManager.fileExists(atPath: itemPath, isDirectory: &isDir) && isDir.boolValue {
                    if fileManager.fileExists(atPath: (itemPath as NSString).appendingPathComponent("manifest.json")) {

                        let attr = try? fileManager.attributesOfItem(atPath: itemPath)
                        let date = attr?[.modificationDate] as? Date

                        results.append(BackupInfo(
                            path: itemPath,
                            name: item,
                            type: "Custom/App",
                            date: date
                        ))
                    }
                }
            }
        } catch {
            // Ignore
        }
        return results
    }
}

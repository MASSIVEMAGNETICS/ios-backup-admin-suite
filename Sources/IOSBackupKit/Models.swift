import Foundation

struct SnapshotManifest: Codable {
    let snapshot_version: String
    let device_id: String
    let created_at: String
    let files_count: Int
    let chunks_count: Int
    let root_merkle: String
    let encryption: String
    let compression: String
}

struct BackupComponent: Codable {
    let name: String
    let format: String
    let files: Int?
    let index: String?
    let info: String?
}

struct FullBackupManifest: Codable {
    let backup_id: String
    let timestamp: String
    let device: DeviceInfo
    let components: [BackupComponent]
    let encryption: EncryptionInfo
    let signature: SignatureInfo?
    let root_hash: String
}

struct DeviceInfo: Codable {
    let model: String
    let os: String
}

struct EncryptionInfo: Codable {
    let scheme: String
    let kdf: String
    let salt: String
}

struct SignatureInfo: Codable {
    let alg: String
    let pub: String
}

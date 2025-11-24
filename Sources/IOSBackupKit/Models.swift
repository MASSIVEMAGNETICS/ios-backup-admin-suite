import Foundation

public struct SnapshotManifest: Codable {
    public let snapshot_version: String
    public let device_id: String
    public let created_at: String
    public let files_count: Int
    public let chunks_count: Int
    public let root_merkle: String
    public let encryption: String
    public let compression: String
    
    public init(snapshot_version: String, device_id: String, created_at: String, files_count: Int, chunks_count: Int, root_merkle: String, encryption: String, compression: String) {
        self.snapshot_version = snapshot_version
        self.device_id = device_id
        self.created_at = created_at
        self.files_count = files_count
        self.chunks_count = chunks_count
        self.root_merkle = root_merkle
        self.encryption = encryption
        self.compression = compression
    }
}

public struct BackupComponent: Codable {
    public let name: String
    public let format: String
    public let files: Int?
    public let index: String?
    public let info: String?
    
    public init(name: String, format: String, files: Int? = nil, index: String? = nil, info: String? = nil) {
        self.name = name
        self.format = format
        self.files = files
        self.index = index
        self.info = info
    }
}

public struct FullBackupManifest: Codable {
    public let backup_id: String
    public let timestamp: String
    public let device: DeviceInfo
    public let components: [BackupComponent]
    public let encryption: EncryptionInfo
    public let signature: SignatureInfo?
    public let root_hash: String
    
    public init(backup_id: String, timestamp: String, device: DeviceInfo, components: [BackupComponent], encryption: EncryptionInfo, signature: SignatureInfo?, root_hash: String) {
        self.backup_id = backup_id
        self.timestamp = timestamp
        self.device = device
        self.components = components
        self.encryption = encryption
        self.signature = signature
        self.root_hash = root_hash
    }
}

public struct DeviceInfo: Codable {
    public let model: String
    public let os: String
    
    public init(model: String, os: String) {
        self.model = model
        self.os = os
    }
}

public struct EncryptionInfo: Codable {
    public let scheme: String
    public let kdf: String
    public let salt: String
    
    public init(scheme: String, kdf: String, salt: String) {
        self.scheme = scheme
        self.kdf = kdf
        self.salt = salt
    }
}

public struct SignatureInfo: Codable {
    public let alg: String
    public let pub: String
    
    public init(alg: String, pub: String) {
        self.alg = alg
        self.pub = pub
    }
}

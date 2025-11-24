import Foundation
import Crypto

// Helper struct for encoding arbitrary types in metadata
public struct AnyCodable: Codable {
    public let value: Any
    
    public init(_ value: Any) {
        self.value = value
    }
    
    public init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if let intVal = try? container.decode(Int.self) {
            value = intVal
        } else if let doubleVal = try? container.decode(Double.self) {
            value = doubleVal
        } else if let stringVal = try? container.decode(String.self) {
            value = stringVal
        } else if let boolVal = try? container.decode(Bool.self) {
            value = boolVal
        } else if let arrayVal = try? container.decode([AnyCodable].self) {
            value = arrayVal.map { $0.value }
        } else if let dictVal = try? container.decode([String: AnyCodable].self) {
            value = dictVal.mapValues { $0.value }
        } else {
            throw DecodingError.dataCorruptedError(in: container, debugDescription: "Unsupported type")
        }
    }
    
    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        if let intVal = value as? Int {
            try container.encode(intVal)
        } else if let doubleVal = value as? Double {
            try container.encode(doubleVal)
        } else if let stringVal = value as? String {
            try container.encode(stringVal)
        } else if let boolVal = value as? Bool {
            try container.encode(boolVal)
        } else if let arrayVal = value as? [Any] {
            try container.encode(arrayVal.map { AnyCodable($0) })
        } else if let dictVal = value as? [String: Any] {
            try container.encode(dictVal.mapValues { AnyCodable($0) })
        } else {
            throw EncodingError.invalidValue(value, EncodingError.Context(codingPath: [], debugDescription: "Unsupported type"))
        }
    }
}

public struct FileEntry: Codable {
    public let path: String
    public let size: Int
    public let chunkHashes: [String]
    public let fileSHA256: String
    public let metadata: [String:AnyCodable]
    public let merkleRoot: String
    
    public init(path: String, size: Int, chunkHashes: [String], fileSHA256: String, metadata: [String:AnyCodable], merkleRoot: String) {
        self.path = path
        self.size = size
        self.chunkHashes = chunkHashes
        self.fileSHA256 = fileSHA256
        self.metadata = metadata
        self.merkleRoot = merkleRoot
    }
}

public final class ManifestBuilder {
    public static func merkleRoot(from hashes: [String]) -> String {
        func hashPair(_ a: Data, _ b: Data) -> Data {
            var d = Data()
            d.append(a)
            d.append(b)
            return Data(SHA256.hash(data: d))
        }
        var nodes = hashes.map { Data(hex: $0) }
        if nodes.isEmpty {
            return Data(SHA256.hash(data: Data())).hex
        }
        while nodes.count > 1 {
            var next: [Data] = []
            for i in stride(from: 0, to: nodes.count, by: 2) {
                if i+1 < nodes.count {
                    next.append(hashPair(nodes[i], nodes[i+1]))
                } else {
                    // duplicate last
                    next.append(hashPair(nodes[i], nodes[i]))
                }
            }
            nodes = next
        }
        return nodes[0].hex
    }
}

// Data extension for hex conversion
extension Data {
    init(hex: String) {
        var data = Data()
        var temp = ""
        for char in hex {
            temp.append(char)
            if temp.count == 2 {
                if let byte = UInt8(temp, radix: 16) {
                    data.append(byte)
                }
                temp = ""
            }
        }
        self = data
    }
    
    var hex: String {
        return self.map { String(format: "%02x", $0) }.joined()
    }
}

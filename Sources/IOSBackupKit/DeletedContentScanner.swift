import Foundation

public class DeletedContentScanner {

    public struct ScannedMessage: CustomStringConvertible {
        public let content: String
        public let offset: Int
        public let context: String

        public var description: String {
            return "Found at offset \(offset): \"\(content)\""
        }
    }

    /// Scans a file for potential text strings that might represent deleted content.
    /// This is a simple heuristic scanner.
    public static func scanFile(path: String, minLength: Int = 5) throws -> [ScannedMessage] {
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: path)) else {
            throw NSError(domain: "DeletedContentScanner", code: 404, userInfo: [NSLocalizedDescriptionKey: "File not found: \(path)"])
        }

        var results: [ScannedMessage] = []
        let count = data.count

        // Convert to array for faster access
        let bytes = [UInt8](data)

        var currentStringBytes: [UInt8] = []
        var startIndex = 0

        // Simple printable ASCII scanner
        // In a real scenario, we'd look for SQLite cell headers or specific binary structures.
        for i in 0..<count {
            let byte = bytes[i]
            // Check for printable ASCII (0x20 - 0x7E)
            if byte >= 0x20 && byte <= 0x7E {
                if currentStringBytes.isEmpty {
                    startIndex = i
                }
                currentStringBytes.append(byte)
            } else {
                if currentStringBytes.count >= minLength {
                    if let string = String(bytes: currentStringBytes, encoding: .ascii) {
                        // Filter out common noise
                        if !isNoise(string) {
                            results.append(ScannedMessage(content: string, offset: startIndex, context: "ASCII"))
                        }
                    }
                }
                currentStringBytes.removeAll()
            }
        }

        return results
    }

    private static func isNoise(_ s: String) -> Bool {
        // Filter out strings that are likely not user content (e.g., property lists keys, SQL keywords)
        let noise = ["CREATE", "TABLE", "INSERT", "INTO", "VALUES", "index", "trigger", "view", "integer", "primary", "key", "autoincrement"]
        if noise.contains(where: { s.localizedCaseInsensitiveContains($0) }) {
            return true
        }
        // Filter out strings with only special chars
        if s.rangeOfCharacter(from: CharacterSet.alphanumerics) == nil {
            return true
        }
        return false
    }
}

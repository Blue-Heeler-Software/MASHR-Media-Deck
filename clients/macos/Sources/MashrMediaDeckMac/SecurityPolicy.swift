import Foundation

enum SecurityPolicy {
    static let companionPort: UInt16 = 43821
    static let discoveryPort: UInt16 = 43822
    static let networkTransportEnabled = false

    static func isSafeScaffoldBind(_ host: String) -> Bool {
        host == "127.0.0.1" || host == "::1" || host.lowercased() == "localhost"
    }
}


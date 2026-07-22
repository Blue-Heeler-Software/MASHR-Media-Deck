import Foundation

struct MediaChapter: Codable {
    let positionMs: Int64
    let title: String
}

struct MediaSnapshot: Codable {
    let title: String
    let artist: String
    let source: String
    let playing: Bool
    let positionMs: Int64
    let durationMs: Int64
    let shuffle: Bool
    let `repeat`: String
    let youtubeAvailable: Bool
    let youtubeVolume: Int
    let chapters: [MediaChapter]
    let instantReplayAvailable: Bool
    let instantReplayEnabled: Bool
    let instantReplaySeconds: Int
}

struct CommandResponse: Codable {
    let action: String
    let provider: String
}

enum ProviderError: LocalizedError {
    case noSupportedPlayer
    case unsupportedCommand(String)
    case appleScript(String)
    case malformedResponse

    var errorDescription: String? {
        switch self {
        case .noSupportedPlayer:
            return "Start Apple Music or Spotify, then try again."
        case .unsupportedCommand(let command):
            return "Unsupported macOS scaffold command: \(command)"
        case .appleScript(let message):
            return message
        case .malformedResponse:
            return "The media provider returned an unexpected response."
        }
    }
}

final class AppleScriptMediaProvider {
    private let runner = AppleScriptRunner()
    private let commands: Set<String> = ["play", "pause", "stop", "next", "previous", "back10", "forward10"]

    func probe() throws -> MediaSnapshot {
        let output = try runner.run(Self.probeScript).trimmingCharacters(in: .whitespacesAndNewlines)
        if output == "NOT_RUNNING" { throw ProviderError.noSupportedPlayer }
        let fields = output.components(separatedBy: "\u{001F}")
        guard fields.count == 6 else { throw ProviderError.malformedResponse }
        let positionMs = Int64((Double(fields[4]) ?? 0) * 1000)
        let durationMs = Int64((Double(fields[5]) ?? 0) * 1000)
        return MediaSnapshot(
            title: fields[1].isEmpty ? "Nothing playing" : fields[1],
            artist: fields[2],
            source: "macos:\(fields[0])",
            playing: fields[3].lowercased() == "true",
            positionMs: max(0, min(positionMs, max(positionMs, durationMs))),
            durationMs: max(0, durationMs),
            shuffle: false,
            repeat: "none",
            youtubeAvailable: false,
            youtubeVolume: -1,
            chapters: [],
            instantReplayAvailable: false,
            instantReplayEnabled: false,
            instantReplaySeconds: 0)
    }

    func control(_ command: String) throws {
        guard commands.contains(command) else { throw ProviderError.unsupportedCommand(command) }
        let output = try runner.run(Self.controlScript(command)).trimmingCharacters(in: .whitespacesAndNewlines)
        if output == "NOT_RUNNING" { throw ProviderError.noSupportedPlayer }
        if output != "OK" { throw ProviderError.malformedResponse }
    }

    private static let probeScript = #"""
    set sep to ASCII character 31
    if application "Music" is running then
        tell application "Music"
            if not (exists current track) then return "NOT_RUNNING"
            return "Music" & sep & (name of current track) & sep & (artist of current track) & sep & ((player state is playing) as text) & sep & (player position as text) & sep & (duration of current track as text)
        end tell
    else if application "Spotify" is running then
        tell application "Spotify"
            return "Spotify" & sep & (name of current track) & sep & (artist of current track) & sep & ((player state is playing) as text) & sep & (player position as text) & sep & (duration of current track / 1000 as text)
        end tell
    end if
    return "NOT_RUNNING"
    """#

    private static func controlScript(_ command: String) -> String {
        let action: String
        switch command {
        case "play": action = "play"
        case "pause", "stop": action = "pause"
        case "next": action = "next track"
        case "previous": action = "previous track"
        case "back10": action = "set player position to (my maxValue(0, player position - 10))"
        case "forward10": action = "set player position to (player position + 10)"
        default: preconditionFailure("Command was not allowlisted")
        }
        return """
        on maxValue(leftValue, rightValue)
            if leftValue > rightValue then return leftValue
            return rightValue
        end maxValue
        if application "Music" is running then
            tell application "Music" to \(action)
            return "OK"
        else if application "Spotify" is running then
            tell application "Spotify" to \(action)
            return "OK"
        end if
        return "NOT_RUNNING"
        """
    }
}

private final class AppleScriptRunner {
    func run(_ script: String) throws -> String {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        process.arguments = ["-e", script]
        let standardOutput = Pipe()
        let standardError = Pipe()
        process.standardOutput = standardOutput
        process.standardError = standardError
        try process.run()
        process.waitUntilExit()
        let output = standardOutput.fileHandleForReading.readDataToEndOfFile()
        let error = standardError.fileHandleForReading.readDataToEndOfFile()
        guard process.terminationStatus == 0 else {
            let message = String(data: error, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines)
            throw ProviderError.appleScript(message?.isEmpty == false ? message! : "AppleScript failed.")
        }
        return String(data: output, encoding: .utf8) ?? ""
    }
}


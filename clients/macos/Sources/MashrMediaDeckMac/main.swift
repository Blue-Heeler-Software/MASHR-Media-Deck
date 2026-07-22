import Darwin
import Foundation

let arguments = Array(CommandLine.arguments.dropFirst())
let provider = AppleScriptMediaProvider()
let encoder = JSONEncoder()
encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]

func printHelp() {
    print("MASHR Media Deck macOS provider scaffold")
    print("  probe")
    print("  command play|pause|stop|next|previous|back10|forward10")
    print("")
    print("No phone listener is enabled in this scaffold.")
}

do {
    switch arguments {
    case [], ["--help"], ["-h"], ["help"]:
        printHelp()
        exit(0)
    case ["probe"]:
        let snapshot = try provider.probe()
        FileHandle.standardOutput.write(try encoder.encode(snapshot))
        print("")
        exit(0)
    case ["command", let command]:
        try provider.control(command)
        let response = CommandResponse(action: command, provider: "AppleScript")
        FileHandle.standardOutput.write(try encoder.encode(response))
        print("")
        exit(0)
    default:
        fputs("Unknown arguments. Run with --help.\n", stderr)
        exit(2)
    }
} catch {
    fputs("\(error.localizedDescription)\n", stderr)
    exit(3)
}


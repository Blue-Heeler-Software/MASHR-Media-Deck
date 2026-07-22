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
    if arguments.isEmpty || arguments == ["--help"] || arguments == ["-h"] || arguments == ["help"] {
        printHelp()
        exit(0)
    } else if arguments == ["probe"] {
        let snapshot = try provider.probe()
        FileHandle.standardOutput.write(try encoder.encode(snapshot))
        print("")
        exit(0)
    } else if arguments.count == 2 && arguments[0] == "command" {
        let command = arguments[1]
        try provider.control(command)
        let response = CommandResponse(action: command, provider: "AppleScript")
        FileHandle.standardOutput.write(try encoder.encode(response))
        print("")
        exit(0)
    } else {
        fputs("Unknown arguments. Run with --help.\n", stderr)
        exit(2)
    }
} catch {
    fputs("\(error.localizedDescription)\n", stderr)
    exit(3)
}

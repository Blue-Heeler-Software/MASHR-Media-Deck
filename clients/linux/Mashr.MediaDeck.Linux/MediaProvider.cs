using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Mashr.MediaDeck.Linux;

public sealed record MediaChapter(long PositionMs, string Title);

public sealed record MediaSnapshot(
    string Title,
    string Artist,
    string Source,
    bool Playing,
    long PositionMs,
    long DurationMs,
    bool Shuffle,
    string Repeat,
    bool YouTubeAvailable,
    int YouTubeVolume,
    IReadOnlyList<MediaChapter> Chapters,
    bool InstantReplayAvailable,
    bool InstantReplayEnabled,
    int InstantReplaySeconds);

public sealed record ProviderResult(bool Success, int ExitCode, MediaSnapshot? Snapshot, string Error)
{
    public static ProviderResult Ok(MediaSnapshot snapshot) => new(true, 0, snapshot, "");
    public static ProviderResult Failed(int exitCode, string error) => new(false, exitCode, null, error);
}

public sealed record ControlResult(bool Success, int ExitCode, string Error)
{
    public static ControlResult Ok() => new(true, 0, "");
    public static ControlResult Failed(int exitCode, string error) => new(false, exitCode, error);
}

public sealed class PlayerctlMediaProvider
{
    private const char Separator = '\u001f';
    private const string MetadataTemplate = "{{xesam:title}}\u001f{{xesam:artist}}\u001f{{mpris:length}}\u001f{{playerName}}";

    private static readonly IReadOnlyDictionary<string, string[]> Commands =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["play"] = ["play"],
            ["pause"] = ["pause"],
            ["stop"] = ["stop"],
            ["next"] = ["next"],
            ["previous"] = ["previous"],
            ["back10"] = ["position", "10-"],
            ["forward10"] = ["position", "10+"]
        };

    public async Task<ProviderResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var metadata = await Playerctl.RunAsync(["metadata", "--format", MetadataTemplate], cancellationToken);
        if (metadata.ExitCode != 0)
            return ProviderResult.Failed(metadata.ExitCode, Playerctl.ErrorMessage(metadata));

        var fields = metadata.StdOut.TrimEnd('\r', '\n').Split(Separator);
        if (fields.Length != 4)
            return ProviderResult.Failed(3, "playerctl returned an unexpected metadata shape.");

        var status = await Playerctl.RunAsync(["status"], cancellationToken);
        var position = await Playerctl.RunAsync(["position"], cancellationToken);
        var positionMs = ParseSeconds(position.StdOut);
        var durationMs = ParseMicroseconds(fields[2]);
        var snapshot = new MediaSnapshot(
            Title: string.IsNullOrWhiteSpace(fields[0]) ? "Nothing playing" : fields[0].Trim(),
            Artist: fields[1].Trim(),
            Source: $"mpris:{fields[3].Trim()}",
            Playing: status.ExitCode == 0 && status.StdOut.Trim().Equals("Playing", StringComparison.OrdinalIgnoreCase),
            PositionMs: Math.Clamp(positionMs, 0, Math.Max(positionMs, durationMs)),
            DurationMs: Math.Max(0, durationMs),
            Shuffle: false,
            Repeat: "none",
            YouTubeAvailable: false,
            YouTubeVolume: -1,
            Chapters: [],
            InstantReplayAvailable: false,
            InstantReplayEnabled: false,
            InstantReplaySeconds: 0);
        return ProviderResult.Ok(snapshot);
    }

    public async Task<ControlResult> ControlAsync(string command, CancellationToken cancellationToken = default)
    {
        if (!Commands.TryGetValue(command, out var arguments))
            return ControlResult.Failed(2, $"Unsupported Linux scaffold command: {command}");
        var result = await Playerctl.RunAsync(arguments, cancellationToken);
        return result.ExitCode == 0
            ? ControlResult.Ok()
            : ControlResult.Failed(result.ExitCode, Playerctl.ErrorMessage(result));
    }

    private static long ParseSeconds(string value) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
            ? (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero)
            : 0;

    private static long ParseMicroseconds(string value) =>
        long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds) && microseconds >= 0
            ? microseconds / 1000
            : 0;
}

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

internal static class Playerctl
{
    public static async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("playerctl")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start);
            if (process is null) return new ProcessResult(127, "", "Could not start playerctl.");
            var standardOut = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new ProcessResult(process.ExitCode, await standardOut, await standardError);
        }
        catch (Win32Exception)
        {
            return new ProcessResult(127, "", "playerctl was not found. Install it from your Linux distribution.");
        }
    }

    public static string ErrorMessage(ProcessResult result) =>
        string.IsNullOrWhiteSpace(result.StdErr) ? $"playerctl failed with exit code {result.ExitCode}." : result.StdErr.Trim();
}


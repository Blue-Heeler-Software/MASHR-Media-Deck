using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using System.Windows.Automation;
using Microsoft.AspNetCore.RateLimiting;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

const int HttpPort = 43821;
const int DiscoveryPort = 43822;
var launchPairingMode = args.Any(argument => argument.Equals("--pairing-mode", StringComparison.OrdinalIgnoreCase));

if (args.Length > 0 && args[0] == "--configure-lan")
{
    try
    {
        if (args.Length != 3) throw new ArgumentException("Expected mode and phone address.");
        LanAccessPolicy.Configure(args[1], args[2]);
    }
    catch { Environment.ExitCode = 2; }
    return;
}
if (args.Length == 1 && args[0] == "--disable-lan")
{
    LanAccessPolicy.Disable();
    return;
}

var lanAccess = LanAccessPolicy.Load();

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls(lanAccess.IsEnabled
    ? $"http://127.0.0.1:{HttpPort};http://{lanAccess.LocalAddress}:{HttpPort}"
    : $"http://127.0.0.1:{HttpPort}");
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 32 * 1024;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 240,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("pair", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("nearby-pair", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 90,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

var app = builder.Build();
var security = new SecurityState();
if (launchPairingMode) security.StartPairing();
var youtube = new YouTubeBridge();
var youtubeChapters = new YouTubeChapterProvider();
var gameWindow = new GameWindowTracker();
string? preferredSource = null;
bool preferVlc = false;

app.UseRateLimiter();
app.Use(async (context, next) =>
{
    var remote = context.Connection.RemoteIpAddress;
    if (!lanAccess.Allows(remote))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    var path = context.Request.Path;
    if (path.StartsWithSegments("/api/browser"))
    {
        if (!NetworkPolicy.IsLoopback(remote))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(context);
        return;
    }

    if (path == "/" || path == "/api/health" || path == "/api/pair" || path == "/api/pair/nearby")
    {
        await next(context);
        return;
    }

    if (!security.Verify(context.Request))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "Pairing or a valid signed request is required." });
        return;
    }

    await next(context);
});

bool MatchesForeground(string source, string process)
{
    var value = source.ToLowerInvariant();
    process = process.ToLowerInvariant();
    return value.Contains(process) || (process == "vlc" && (value.Contains("videolan") || value.Contains("vlc")));
}

async Task<GlobalSystemMediaTransportControlsSession?> Session()
{
    var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
    var sessions = manager.GetSessions();
    var foreground = ForegroundApp.ProcessName();
    if (foreground.Equals("vlc", StringComparison.OrdinalIgnoreCase) && VlcProvider.Exists())
    {
        preferVlc = true;
        preferredSource = null;
        return null;
    }
    var focused = sessions.FirstOrDefault(session => MatchesForeground(session.SourceAppUserModelId, foreground));
    if (focused is not null)
    {
        preferVlc = false;
        preferredSource = focused.SourceAppUserModelId;
    }
    if (preferVlc && VlcProvider.Exists()) return null;
    var preferred = sessions.FirstOrDefault(session => session.SourceAppUserModelId == preferredSource);
    if (preferred is not null) return preferred;
    var playing = sessions.FirstOrDefault(session => session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
    if (playing is not null)
    {
        preferredSource = playing.SourceAppUserModelId;
        return playing;
    }
    return manager.GetCurrentSession();
}

app.MapGet("/api/health", () => Results.Json(new
{
    service = "MASHR Media Deck",
    paired = security.IsPaired,
    pairedDevices = security.DeviceCount,
    nearbyPairingRequests = security.NearbyRequestCount,
    pairingOpen = security.IsPairingOpen,
    pairingSecondsRemaining = security.PairingSecondsRemaining,
    lanMode = lanAccess.Mode,
    bindAddress = lanAccess.IsEnabled ? lanAccess.LocalAddress?.ToString() : "loopback"
}));
app.MapPost("/api/pair", (HttpContext context) =>
{
    var code = context.Request.Headers["X-MediaDeck-Pairing-Code"].ToString();
    var deviceId = context.Request.Headers["X-MediaDeck-Device"].ToString();
    var deviceName = context.Request.Headers["X-MediaDeck-Device-Name"].ToString();
    if (security.TryPair(code, deviceId, deviceName, context.Connection.RemoteIpAddress, out var key, out var assignedDeviceId))
        return Results.Json(new { key, deviceId = assignedDeviceId, algorithm = "HMAC-SHA256", clockWindowSeconds = 30 });
    return security.IsPairingOpen
        ? Results.Json(new { error = "Pairing code is incorrect." }, statusCode: StatusCodes.Status401Unauthorized)
        : Results.Json(new { error = "Pairing is closed. Open the PC dashboard and start pairing mode." }, statusCode: StatusCodes.Status409Conflict);
}).RequireRateLimiting("pair");

app.MapPost("/api/pair/nearby", (HttpContext context) =>
{
    var deviceId = context.Request.Headers["X-MediaDeck-Device"].ToString();
    var deviceName = context.Request.Headers["X-MediaDeck-Device-Name"].ToString();
    var requestToken = context.Request.Headers["X-MediaDeck-Pairing-Request"].ToString();
    var result = security.TryNearbyPair(deviceId, deviceName, requestToken, context.Connection.RemoteIpAddress, out var key, out var expiresInSeconds);
    return result switch
    {
        NearbyPairingStatus.Paired => Results.Json(new { status = "paired", key, deviceId, algorithm = "HMAC-SHA256", clockWindowSeconds = 30 }),
        NearbyPairingStatus.Waiting => Results.Json(new { status = "waiting", expiresInSeconds }, statusCode: StatusCodes.Status202Accepted),
        NearbyPairingStatus.Capacity => Results.Json(new { error = "The PC has too many pending or paired controllers. Revoke one in the dashboard and try again." }, statusCode: StatusCodes.Status429TooManyRequests),
        _ => Results.Json(new { error = "Invalid nearby pairing request." }, statusCode: StatusCodes.Status400BadRequest)
    };
}).RequireRateLimiting("nearby-pair");

app.MapGet("/api/now", async (HttpContext context) =>
{
    var replay = NvidiaReplayState.Read();
    var session = await Session();
    if (preferVlc && VlcProvider.TryInfo(out var vlc))
        return Results.Json(new { title = vlc.Title, artist = "VLC media player", source = "VLC.PC", playing = vlc.Playing, positionMs = 0L, durationMs = 0L, shuffle = false, repeat = "none", youtubeAvailable = youtube.IsFresh, youtubeVolume = -1, chapters = Array.Empty<MediaChapter>(), instantReplayAvailable = replay.Available, instantReplayEnabled = replay.Enabled, instantReplaySeconds = replay.BufferSeconds });
    if (session is null)
        return Results.Json(new { title = "Nothing playing", artist = "Start YouTube Music or another player on this PC", source = "Windows", playing = false, positionMs = 0L, durationMs = 0L, shuffle = false, repeat = "none", youtubeAvailable = youtube.IsFresh, youtubeVolume = -1, chapters = Array.Empty<MediaChapter>(), instantReplayAvailable = replay.Available, instantReplayEnabled = replay.Enabled, instantReplaySeconds = replay.BufferSeconds });
    var media = await session.TryGetMediaPropertiesAsync();
    var playback = session.GetPlaybackInfo();
    var timeline = session.GetTimelineProperties();
    var duration = Math.Max(0, (timeline.EndTime - timeline.StartTime).Ticks / TimeSpan.TicksPerMillisecond);
    var position = TimelineClock.PositionMs(playback, timeline);
    var chapters = await youtubeChapters.GetAsync(session.SourceAppUserModelId, media.Title, duration, context.RequestAborted);
    var youtubeVolume = YouTubeActions.ReadVolume(session.SourceAppUserModelId, media.Title);
    return Results.Json(new
    {
        title = media.Title,
        artist = string.IsNullOrWhiteSpace(media.Artist) ? media.AlbumArtist : media.Artist,
        source = session.SourceAppUserModelId,
        playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
        positionMs = Math.Min(position, duration),
        durationMs = duration,
        shuffle = playback.IsShuffleActive ?? false,
        repeat = (playback.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None).ToString().ToLowerInvariant(),
        youtubeAvailable = youtube.IsFresh,
        youtubeVolume,
        chapters,
        instantReplayAvailable = replay.Available,
        instantReplayEnabled = replay.Enabled,
        instantReplaySeconds = replay.BufferSeconds
    });
});

app.MapGet("/api/art", async () =>
{
    var session = await Session();
    if (preferVlc)
    {
        var capture = VlcProvider.Capture();
        return capture is null ? Results.NotFound() : Results.Bytes(capture, "image/jpeg");
    }
    if (session is null) return Results.NotFound();
    var media = await session.TryGetMediaPropertiesAsync();
    if (media.Thumbnail is null) return Results.NotFound();
    using var stream = await media.Thumbnail.OpenReadAsync();
    var bytes = new byte[stream.Size];
    using var reader = new DataReader(stream);
    await reader.LoadAsync((uint)stream.Size);
    reader.ReadBytes(bytes);
    return Results.Bytes(bytes, "image/jpeg");
});

app.MapGet("/api/sessions", async () =>
{
    var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
    var result = new List<object>();
    foreach (var session in manager.GetSessions())
    {
        var media = await session.TryGetMediaPropertiesAsync();
        result.Add(new { source = session.SourceAppUserModelId, title = media.Title, artist = media.Artist, playing = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, selected = session.SourceAppUserModelId == preferredSource });
    }
    return Results.Json(result);
});

app.MapPost("/api/sessions/select", async (string source) =>
{
    var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
    if (!manager.GetSessions().Any(session => session.SourceAppUserModelId == source)) return Results.NotFound();
    preferredSource = source;
    return Results.Ok();
});

app.MapPost("/api/seek", async (long positionMs) =>
{
    var session = await Session();
    if (session is null) return Results.NotFound();
    var timeline = session.GetTimelineProperties();
    var target = timeline.StartTime.Ticks + positionMs * TimeSpan.TicksPerMillisecond;
    target = Math.Clamp(target, timeline.MinSeekTime.Ticks, timeline.MaxSeekTime.Ticks);
    return await session.TryChangePlaybackPositionAsync(target) ? Results.Ok() : Results.BadRequest();
});

app.MapPost("/api/skip", async (int seconds) =>
{
    if (seconds == 0 || seconds is < -120 or > 120)
        return Results.BadRequest(new { error = "Skip must be from 1 to 120 seconds in either direction." });
    if (preferVlc)
        return VlcProvider.Skip(seconds) ? Results.Ok() : Results.BadRequest();
    var session = await Session();
    if (session is null) return Results.NotFound();
    var playback = session.GetPlaybackInfo();
    var timeline = session.GetTimelineProperties();
    var target = TimelineClock.PositionTicks(playback, timeline) + TimeSpan.FromSeconds(seconds).Ticks;
    target = Math.Clamp(target, timeline.MinSeekTime.Ticks, timeline.MaxSeekTime.Ticks);
    return await session.TryChangePlaybackPositionAsync(target) ? Results.Ok() : Results.BadRequest();
});

app.MapPost("/api/control/{command}", async (string command) =>
{
    if (command == "alttab") { MediaKeys.AltTab(); return Results.Ok(); }
    if (command == "screenshot")
    {
        var screenshotKeys = NvidiaReplayState.Read().ScreenshotKeys;
        if (screenshotKeys.Length > 0)
        {
            MediaKeys.Hotkey(screenshotKeys);
            return Results.Json(new { action = "captured", provider = "NVIDIA Overlay" });
        }
        MediaKeys.Hotkey([0x5B, 0x2C]);
        return Results.Json(new { action = "captured", provider = "Windows" });
    }
    if (command == "instantreplay")
    {
        var replay = NvidiaReplayState.Read();
        if (!replay.Available) return Results.Json(new { error = "NVIDIA Instant Replay is not available on this PC." }, statusCode: StatusCodes.Status409Conflict);
        if (!replay.Enabled) return Results.Json(new { error = "NVIDIA Instant Replay is off. Swipe once to arm it, then save clips after the buffer has filled." }, statusCode: StatusCodes.Status409Conflict);
        var focus = await gameWindow.FocusForReplayAsync();
        if (!focus.Success) return Results.Json(new { error = focus.Error }, statusCode: StatusCodes.Status409Conflict);
        MediaKeys.Hotkey(replay.SaveKeys);
        return Results.Json(new { action = "saved", bufferSeconds = replay.BufferSeconds, gameFocused = true, gameRefocused = focus.Refocused });
    }
    if (command == "replayarm")
    {
        var replay = NvidiaReplayState.Read();
        if (!replay.Available) return Results.Json(new { error = "NVIDIA Instant Replay is not available on this PC." }, statusCode: StatusCodes.Status409Conflict);
        if (replay.Enabled) return Results.Json(new { action = "already-armed", bufferSeconds = replay.BufferSeconds });
        MediaKeys.Hotkey(replay.ToggleKeys);
        return Results.Json(new { action = "arming", bufferSeconds = replay.BufferSeconds });
    }
    if (command == "altdown") { MediaKeys.BeginAltTab(); return Results.Ok(); }
    if (command == "altup") { MediaKeys.EndAltTab(); return Results.Ok(); }
    if (command is "arrowleft" or "arrowright") { MediaKeys.SwitcherArrow(command == "arrowleft" ? -1 : 1); return Results.Ok(); }
    if (command is "mute" or "volumeup" or "volumedown")
    {
        MediaKeys.Tap(command switch { "mute" => 0xAD, "volumedown" => 0xAE, _ => 0xAF });
        return Results.Ok();
    }
    var session = await Session();
    if (command is "like" or "dislike" or "subscribe")
    {
        if (preferVlc || session is null)
            return Results.Json(new { error = "Select a YouTube or YouTube Music session first." }, statusCode: StatusCodes.Status409Conflict);
        var media = await session.TryGetMediaPropertiesAsync();
        var result = YouTubeActions.Invoke(session.SourceAppUserModelId, media.Title, command);
        return result.Success
            ? Results.Json(new { action = result.Action, message = result.Message })
            : Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status409Conflict);
    }
    if (command == "movescreen")
    {
        WindowMoveResult move;
        if (preferVlc && VlcProvider.TryInfo(out var vlc)) move = WindowMover.MoveToNext("VLC.PC", vlc.Title);
        else if (session is not null)
        {
            var media = await session.TryGetMediaPropertiesAsync();
            move = WindowMover.MoveToNext(session.SourceAppUserModelId, media.Title);
        }
        else return Results.NotFound(new { error = "No selected media window is available." });
        return move.Success
            ? Results.Json(new { display = move.Display, monitor = move.Monitor, monitorCount = move.MonitorCount })
            : Results.Json(new { error = move.Error }, statusCode: StatusCodes.Status409Conflict);
    }
    if (preferVlc) return VlcProvider.Control(command) ? Results.Ok() : Results.BadRequest();
    if (session is null) return Results.NotFound();
    var playback = session.GetPlaybackInfo();
    var timeline = session.GetTimelineProperties();
    var ok = command switch
    {
        "play" => await session.TryPlayAsync(),
        "pause" => await session.TryPauseAsync(),
        "stop" => await session.TryStopAsync(),
        "next" => await session.TrySkipNextAsync(),
        "previous" => await session.TrySkipPreviousAsync(),
        "back10" => await session.TryChangePlaybackPositionAsync(Math.Max(timeline.MinSeekTime.Ticks, TimelineClock.PositionTicks(playback, timeline) - TimeSpan.FromSeconds(10).Ticks)),
        "forward10" => await session.TryChangePlaybackPositionAsync(Math.Min(timeline.MaxSeekTime.Ticks, TimelineClock.PositionTicks(playback, timeline) + TimeSpan.FromSeconds(10).Ticks)),
        "shuffle" => await session.TryChangeShuffleActiveAsync(!(playback.IsShuffleActive ?? false)),
        "repeat" => await session.TryChangeAutoRepeatModeAsync((playback.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None) switch { MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.Track, MediaPlaybackAutoRepeatMode.Track => MediaPlaybackAutoRepeatMode.List, _ => MediaPlaybackAutoRepeatMode.None }),
        _ => false
    };
    return ok ? Results.Ok() : Results.BadRequest();
});

app.MapGet("/api/youtube/suggestions", () => Results.Json(youtube.Snapshot()));
app.MapPost("/api/youtube/play", (string videoId) => youtube.Queue(videoId) ? Results.Ok() : Results.BadRequest(new { error = "That video is not in the current recommendation grid." }));

app.MapPost("/api/youtube/volume", async (int level) =>
{
    if (level is < 0 or > 100) return Results.BadRequest(new { error = "YouTube volume must be from 0 to 100." });
    var session = await Session();
    if (preferVlc || session is null)
        return Results.Json(new { error = "Select a YouTube or YouTube Music session first." }, statusCode: StatusCodes.Status409Conflict);
    var media = await session.TryGetMediaPropertiesAsync();
    var result = YouTubeActions.SetVolume(session.SourceAppUserModelId, media.Title, level);
    return result.Success
        ? Results.Json(new { volume = result.Volume, message = result.Message })
        : Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status409Conflict);
});

app.MapPost("/api/browser/youtube/state", async (HttpRequest request) =>
{
    try
    {
        using var document = await JsonDocument.ParseAsync(request.Body);
        var items = new List<YouTubeSuggestion>();
        if (document.RootElement.TryGetProperty("suggestions", out var suggestions) && suggestions.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in suggestions.EnumerateArray().Take(9))
            {
                var videoId = item.TryGetProperty("videoId", out var idNode) ? idNode.GetString() : null;
                var title = item.TryGetProperty("title", out var titleNode) ? titleNode.GetString() : null;
                if (!YouTubeBridge.ValidVideoId(videoId) || string.IsNullOrWhiteSpace(title)) continue;
                items.Add(new YouTubeSuggestion(videoId!, title.Trim()[..Math.Min(title.Trim().Length, 120)], $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg"));
            }
        }
        youtube.Update(items);
        return Results.Ok();
    }
    catch (JsonException)
    {
        return Results.BadRequest();
    }
});
app.MapGet("/api/browser/youtube/command", () => Results.Json(new { videoId = youtube.TakeCommand() }));

app.MapGet("/", () => "MASHR Media Deck Companion");
_ = gameWindow.Run(app.Lifetime.ApplicationStopping);
if (lanAccess.IsEnabled) _ = LanDiscovery.Run(DiscoveryPort, HttpPort, lanAccess, app.Lifetime.ApplicationStopping);
TrayApplication.Start(security, app.Lifetime, lanAccess);
app.Run();

sealed class SecurityState
{
    private const int PairingWindowSeconds = 120;
    private const int MaximumDevices = 16;
    private const int NearbyRequestSeconds = 45;
    private const int NearbyGrantSeconds = 30;
    private const int MaximumNearbyRequests = 12;
    private const string LegacyDeviceId = "legacy";
    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, long> usedNonces = new();
    private readonly string directory;
    private readonly string legacyKeyPath;
    private readonly string legacyPairedPath;
    private readonly string devicesPath;
    private readonly string codePath;
    private readonly Dictionary<string, PairedDevice> devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NearbyPairingRequest> nearbyRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NearbyPairingGrant> nearbyGrants = new(StringComparer.Ordinal);
    private string pairingCode = "";
    private DateTimeOffset pairingExpiresAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastDeviceSave = DateTimeOffset.MinValue;

    public SecurityState()
    {
        directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaDeck");
        legacyKeyPath = Path.Combine(directory, "device.key");
        legacyPairedPath = Path.Combine(directory, "paired.marker");
        devicesPath = Path.Combine(directory, "paired-devices.json");
        codePath = Path.Combine(directory, "pairing-code.txt");
        Directory.CreateDirectory(directory);
        var storeExists = File.Exists(devicesPath);
        LoadDevices();
        var migratedLegacy = devices.Count == 0 && File.Exists(legacyPairedPath) && MigrateLegacyPairing();
        if (!storeExists || migratedLegacy) SaveDevicesLocked();
        if (migratedLegacy) DeleteLegacyMarker();
        DeleteCodeFile();
        if (devices.Count == 0) StartPairingLocked(TimeSpan.FromSeconds(PairingWindowSeconds));
    }

    public bool IsPaired { get { lock (gate) return devices.Count > 0; } }
    public int DeviceCount { get { lock (gate) return devices.Count; } }
    public int NearbyRequestCount { get { lock (gate) { ExpireNearbyLocked(); return nearbyRequests.Count; } } }
    public bool IsPairingOpen { get { lock (gate) { ExpirePairingLocked(); return pairingExpiresAt > DateTimeOffset.UtcNow; } } }
    public string PairingCode { get { lock (gate) { ExpirePairingLocked(); return pairingExpiresAt > DateTimeOffset.UtcNow ? pairingCode : ""; } } }
    public int PairingSecondsRemaining
    {
        get
        {
            lock (gate)
            {
                ExpirePairingLocked();
                return Math.Max(0, (int)Math.Ceiling((pairingExpiresAt - DateTimeOffset.UtcNow).TotalSeconds));
            }
        }
    }

    public IReadOnlyList<PairedDeviceView> Snapshot()
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            return devices.Values
                .OrderByDescending(device => device.LastSeenUtc)
                .Select(device => new PairedDeviceView(
                    device.Id,
                    device.Name,
                    device.LastAddress,
                    device.PairedAtUtc,
                    device.LastSeenUtc,
                    now - device.LastSeenUtc < TimeSpan.FromSeconds(12),
                    device.Id == LegacyDeviceId))
                .ToArray();
        }
    }

    public IReadOnlyList<NearbyDeviceView> SnapshotNearby()
    {
        lock (gate)
        {
            ExpireNearbyLocked();
            return nearbyRequests.Values
                .OrderByDescending(request => request.RequestedAtUtc)
                .Select(request => new NearbyDeviceView(
                    request.RequestId,
                    request.DeviceId,
                    request.Name,
                    request.Address,
                    request.RequestedAtUtc,
                    request.ExpiresAtUtc,
                    nearbyGrants.ContainsKey(request.RequestId)))
                .ToArray();
        }
    }

    public void StartPairing() { lock (gate) StartPairingLocked(TimeSpan.FromSeconds(PairingWindowSeconds)); }

    public void StopPairing()
    {
        lock (gate)
        {
            pairingCode = "";
            pairingExpiresAt = DateTimeOffset.MinValue;
            DeleteCodeFile();
        }
    }

    public bool TryPair(string candidate, string requestedId, string requestedName, IPAddress? remoteAddress, out string encodedKey, out string assignedDeviceId)
    {
        lock (gate)
        {
            encodedKey = "";
            assignedDeviceId = "";
            ExpirePairingLocked();
            if (pairingExpiresAt <= DateTimeOffset.UtcNow || candidate.Length != 6 ||
                !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(pairingCode))) return false;

            var id = NormalizeDeviceId(requestedId);
            if (id.Length == 0) id = Guid.NewGuid().ToString("N");
            if (!devices.ContainsKey(id) && devices.Count >= MaximumDevices) return false;
            var now = DateTimeOffset.UtcNow;
            var key = RandomNumberGenerator.GetBytes(32);
            var device = new PairedDevice(
                id,
                NormalizeDeviceName(requestedName),
                key,
                now,
                now,
                NormalizeAddress(remoteAddress));
            devices[id] = device;
            usedNonces.Clear();
            SaveDevicesLocked();
            encodedKey = Convert.ToBase64String(key);
            assignedDeviceId = id;
            return true;
        }
    }

    public NearbyPairingStatus TryNearbyPair(string requestedId, string requestedName, string requestToken, IPAddress? remoteAddress, out string encodedKey, out int expiresInSeconds)
    {
        encodedKey = "";
        expiresInSeconds = 0;
        var deviceId = NormalizeDeviceId(requestedId);
        var address = NormalizeAddress(remoteAddress);
        requestToken = requestToken.Trim();
        if (deviceId.Length == 0 || address.Length == 0 || requestToken.Length is < 32 or > 64 ||
            requestToken.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_')) return NearbyPairingStatus.Invalid;

        var requestId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{deviceId}\n{requestToken}"))).ToLowerInvariant();
        lock (gate)
        {
            ExpireNearbyLocked();
            var now = DateTimeOffset.UtcNow;
            if (nearbyGrants.TryGetValue(requestId, out var grant))
            {
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(grant.Address), Encoding.ASCII.GetBytes(address)))
                    return NearbyPairingStatus.Invalid;
                if (!devices.ContainsKey(deviceId) && devices.Count >= MaximumDevices) return NearbyPairingStatus.Capacity;

                devices[deviceId] = new PairedDevice(deviceId, grant.Name, grant.Key, now, now, address);
                foreach (var related in nearbyRequests.Values.Where(request => request.DeviceId == deviceId).Select(request => request.RequestId).ToArray())
                {
                    nearbyRequests.Remove(related);
                    nearbyGrants.Remove(related);
                }
                usedNonces.Clear();
                SaveDevicesLocked();
                encodedKey = Convert.ToBase64String(grant.Key);
                return NearbyPairingStatus.Paired;
            }

            if (!nearbyRequests.ContainsKey(requestId) && nearbyRequests.Count >= MaximumNearbyRequests) return NearbyPairingStatus.Capacity;
            var existing = nearbyRequests.GetValueOrDefault(requestId);
            nearbyRequests[requestId] = new NearbyPairingRequest(
                requestId,
                deviceId,
                NormalizeDeviceName(requestedName),
                address,
                existing?.RequestedAtUtc ?? now,
                now.AddSeconds(NearbyRequestSeconds));
            expiresInSeconds = NearbyRequestSeconds;
            return NearbyPairingStatus.Waiting;
        }
    }

    public bool ApproveNearby(string requestId)
    {
        lock (gate)
        {
            ExpireNearbyLocked();
            if (!nearbyRequests.TryGetValue(requestId, out var request)) return false;
            if (!devices.ContainsKey(request.DeviceId) && devices.Count >= MaximumDevices) return false;
            var expires = DateTimeOffset.UtcNow.AddSeconds(NearbyGrantSeconds);
            nearbyGrants[requestId] = new NearbyPairingGrant(requestId, request.DeviceId, request.Name, request.Address, RandomNumberGenerator.GetBytes(32), expires);
            nearbyRequests[requestId] = request with { ExpiresAtUtc = expires };
            return true;
        }
    }

    public bool Revoke(string deviceId)
    {
        lock (gate)
        {
            if (!devices.Remove(deviceId)) return false;
            usedNonces.Clear();
            SaveDevicesLocked();
            return true;
        }
    }

    public void RevokeAllAndPair()
    {
        lock (gate)
        {
            devices.Clear();
            usedNonces.Clear();
            SaveDevicesLocked();
            DeleteLegacyMarker();
            StartPairingLocked(TimeSpan.FromSeconds(PairingWindowSeconds));
        }
    }

    public bool Verify(HttpRequest request)
    {
        if (!long.TryParse(request.Headers["X-MediaDeck-Time"], NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp)) return false;
        if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp) > 30) return false;
        var nonce = request.Headers["X-MediaDeck-Nonce"].ToString();
        if (nonce.Length is < 16 or > 64 || nonce.Any(ch => !char.IsAsciiLetterOrDigit(ch))) return false;
        byte[] suppliedBytes;
        try { suppliedBytes = Convert.FromBase64String(request.Headers["X-MediaDeck-Signature"].ToString()); }
        catch (FormatException) { return false; }

        var requestedId = NormalizeDeviceId(request.Headers["X-MediaDeck-Device"].ToString());
        PairedDevice? candidate;
        lock (gate)
        {
            if (requestedId.Length > 0 && devices.TryGetValue(requestedId, out candidate)) { }
            else devices.TryGetValue(LegacyDeviceId, out candidate);
            if (candidate is null) return false;
            candidate = candidate with { Key = candidate.Key.ToArray() };
        }

        var canonical = $"{request.Method.ToUpperInvariant()}\n{request.Path}{request.QueryString}\n{timestamp}\n{nonce}";
        var expected = HMACSHA256.HashData(candidate.Key, Encoding.UTF8.GetBytes(canonical));
        if (!CryptographicOperations.FixedTimeEquals(expected, suppliedBytes)) return false;
        var nonceKey = $"{candidate.Id}:{nonce}";
        if (!usedNonces.TryAdd(nonceKey, timestamp + 35)) return false;

        lock (gate)
        {
            if (!devices.TryGetValue(candidate.Id, out var current)) return false;
            var migratedLegacy = false;
            if (candidate.Id == LegacyDeviceId && requestedId.Length > 0)
            {
                devices.Remove(LegacyDeviceId);
                current = current with
                {
                    Id = requestedId,
                    Name = NormalizeDeviceName(request.Headers["X-MediaDeck-Device-Name"].ToString())
                };
                devices[requestedId] = current;
                migratedLegacy = true;
            }
            var now = DateTimeOffset.UtcNow;
            current = current with { LastSeenUtc = now, LastAddress = NormalizeAddress(request.HttpContext.Connection.RemoteIpAddress) };
            devices[current.Id] = current;
            if (migratedLegacy || now - lastDeviceSave >= TimeSpan.FromMinutes(1)) SaveDevicesLocked();
        }

        if (usedNonces.Count > 1024)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var old in usedNonces.Where(pair => pair.Value < now).Select(pair => pair.Key)) usedNonces.TryRemove(old, out _);
        }
        return true;
    }

    private bool LoadDevices()
    {
        if (!File.Exists(devicesPath)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(devicesPath));
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                try
                {
                    var id = NormalizeDeviceId(JsonText(item, "Id"));
                    var key = Convert.FromBase64String(JsonText(item, "Key"));
                    if (id.Length == 0 || key.Length != 32 || devices.Count >= MaximumDevices) continue;
                    var name = JsonText(item, "Name");
                    var pairedAt = JsonDate(item, "PairedAtUtc", DateTimeOffset.UtcNow);
                    var lastSeen = JsonDate(item, "LastSeenUtc", pairedAt);
                    devices[id] = new PairedDevice(id, NormalizeDeviceName(name), key, pairedAt, lastSeen, JsonText(item, "LastAddress"));
                }
                catch { }
            }
            return true;
        }
        catch (Exception error)
        {
            try { File.WriteAllText(Path.Combine(directory, "pairing-load-error.txt"), $"{DateTimeOffset.UtcNow:O} {error.GetType().Name}: {error.Message}"); } catch { }
            return false;
        }
    }

    private bool MigrateLegacyPairing()
    {
        try
        {
            if (!File.Exists(legacyPairedPath) || !File.Exists(legacyKeyPath)) return false;
            var key = Convert.FromBase64String(File.ReadAllText(legacyKeyPath).Trim());
            if (key.Length != 32) return false;
            var pairedAt = File.GetLastWriteTimeUtc(legacyPairedPath);
            devices[LegacyDeviceId] = new PairedDevice(LegacyDeviceId, "Previously paired phone", key, pairedAt, pairedAt, "");
            return true;
        }
        catch { return false; }
    }

    private void StartPairingLocked(TimeSpan duration)
    {
        pairingCode = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(CultureInfo.InvariantCulture);
        pairingExpiresAt = DateTimeOffset.UtcNow.Add(duration);
        File.WriteAllText(codePath, pairingCode);
    }

    private void ExpirePairingLocked()
    {
        if (pairingExpiresAt <= DateTimeOffset.UtcNow && pairingCode.Length > 0)
        {
            pairingCode = "";
            pairingExpiresAt = DateTimeOffset.MinValue;
            DeleteCodeFile();
        }
    }

    private void ExpireNearbyLocked()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var requestId in nearbyRequests.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToArray())
            nearbyRequests.Remove(requestId);
        foreach (var requestId in nearbyGrants.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToArray())
            nearbyGrants.Remove(requestId);
    }

    private void SaveDevicesLocked()
    {
        try
        {
            var stored = devices.Values.Select(device => new StoredDevice(
                device.Id,
                device.Name,
                Convert.ToBase64String(device.Key),
                device.PairedAtUtc,
                device.LastSeenUtc,
                device.LastAddress)).ToArray();
            var temporary = devicesPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, devicesPath, true);
            lastDeviceSave = DateTimeOffset.UtcNow;
        }
        catch { }
    }

    private static string NormalizeDeviceId(string value)
    {
        value = value.Trim();
        if (value == LegacyDeviceId) return value;
        return value.Length is >= 8 and <= 64 && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.') ? value : "";
    }

    private static string NormalizeDeviceName(string value)
    {
        value = new string(value.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (value.Length == 0) return "Android device";
        return value.Length <= 48 ? value : value[..48];
    }

    private static string NormalizeAddress(IPAddress? address)
    {
        if (address?.IsIPv4MappedToIPv6 == true) address = address.MapToIPv4();
        return address?.ToString() ?? "";
    }

    private static string JsonText(JsonElement item, string name)
    {
        foreach (var property in item.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : "";
        return "";
    }

    private static DateTimeOffset JsonDate(JsonElement item, string name, DateTimeOffset fallback)
    {
        var value = JsonText(item, name);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : fallback;
    }

    private void DeleteCodeFile()
    {
        try { if (File.Exists(codePath)) File.Delete(codePath); } catch { }
    }

    private void DeleteLegacyMarker()
    {
        try { if (File.Exists(legacyPairedPath)) File.Delete(legacyPairedPath); } catch { }
    }

    private sealed record PairedDevice(string Id, string Name, byte[] Key, DateTimeOffset PairedAtUtc, DateTimeOffset LastSeenUtc, string LastAddress);
    private sealed record StoredDevice(string Id, string Name, string Key, DateTimeOffset PairedAtUtc, DateTimeOffset LastSeenUtc, string? LastAddress);
    private sealed record NearbyPairingRequest(string RequestId, string DeviceId, string Name, string Address, DateTimeOffset RequestedAtUtc, DateTimeOffset ExpiresAtUtc);
    private sealed record NearbyPairingGrant(string RequestId, string DeviceId, string Name, string Address, byte[] Key, DateTimeOffset ExpiresAtUtc);
}

sealed record PairedDeviceView(string Id, string Name, string Address, DateTimeOffset PairedAtUtc, DateTimeOffset LastSeenUtc, bool Online, bool Legacy);
sealed record NearbyDeviceView(string RequestId, string DeviceId, string Name, string Address, DateTimeOffset RequestedAtUtc, DateTimeOffset ExpiresAtUtc, bool Approved);
enum NearbyPairingStatus { Invalid, Waiting, Paired, Capacity }

static class NetworkPolicy
{
    public static bool IsLoopback(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }

}

sealed record LanAccessPolicy(string Mode, IPAddress? PhoneAddress, IPAddress? LocalAddress, int PrefixLength)
{
    private static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaDeck");
    private static readonly string ConfigPath = Path.Combine(DirectoryPath, "lan-access.json");
    public bool IsEnabled => Mode is "paired-phone" or "same-subnet" && PhoneAddress is not null && LocalAddress is not null;
    public string Display => Mode switch
    {
        "paired-phone" => $"paired phone {PhoneAddress}",
        "same-subnet" => $"subnet {NetworkCidr}",
        _ => "loopback only"
    };
    public string NetworkCidr => LocalAddress is null ? "" : $"{NetworkAddress(LocalAddress, PrefixLength)}/{PrefixLength}";
    public IPAddress? BroadcastAddress => LocalAddress is null ? null : SubnetBroadcastAddress(LocalAddress, PrefixLength);

    public bool Allows(IPAddress? remote)
    {
        if (remote is null) return false;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (IPAddress.IsLoopback(remote)) return true;
        if (!IsEnabled || remote.AddressFamily != AddressFamily.InterNetwork) return false;
        return Mode == "paired-phone"
            ? remote.Equals(PhoneAddress)
            : SameSubnet(LocalAddress!, remote, PrefixLength);
    }

    public static LanAccessPolicy Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return Disabled();
            using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            var root = document.RootElement;
            var mode = root.GetProperty("mode").GetString() ?? "";
            var phoneText = root.GetProperty("phoneAddress").GetString() ?? "";
            if (mode is not ("paired-phone" or "same-subnet") || !IPAddress.TryParse(phoneText, out var phone) || phone.AddressFamily != AddressFamily.InterNetwork) return Disabled();
            var connection = FindConnection(phone);
            return connection is null ? Disabled() : new(mode, phone, connection.Value.Address, connection.Value.PrefixLength);
        }
        catch { return Disabled(); }
    }

    public static void Configure(string mode, string phoneText)
    {
        if (mode is not ("paired-phone" or "same-subnet")) throw new ArgumentException("Unsupported LAN mode.");
        if (!IPAddress.TryParse(phoneText, out var phone) || phone.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("A valid IPv4 phone address is required.");
        if (FindConnection(phone) is null) throw new ArgumentException("The phone is not on a directly connected PC subnet.");
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new { mode, phoneAddress = phone.ToString() }));
    }

    public static void Disable()
    {
        try { if (File.Exists(ConfigPath)) File.Delete(ConfigPath); } catch { }
    }

    private static LanAccessPolicy Disabled() => new("loopback", null, null, 0);

    private static (IPAddress Address, int PrefixLength)? FindConnection(IPAddress phone)
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up && item.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses.Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                if (SameSubnet(unicast.Address, phone, unicast.PrefixLength)) return (unicast.Address, unicast.PrefixLength);
            }
        }
        return null;
    }

    private static bool SameSubnet(IPAddress left, IPAddress right, int prefixLength)
    {
        var a = left.GetAddressBytes();
        var b = right.GetAddressBytes();
        if (a.Length != 4 || b.Length != 4 || prefixLength is < 0 or > 32) return false;
        var wholeBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        for (var index = 0; index < wholeBytes; index++) if (a[index] != b[index]) return false;
        if (remainingBits == 0) return true;
        var mask = (byte)(0xFF << (8 - remainingBits));
        return (a[wholeBytes] & mask) == (b[wholeBytes] & mask);
    }

    private static IPAddress NetworkAddress(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        for (var index = 0; index < bytes.Length; index++)
        {
            var bits = Math.Clamp(prefixLength - index * 8, 0, 8);
            var mask = bits == 0 ? 0 : (0xFF << (8 - bits)) & 0xFF;
            bytes[index] = (byte)(bytes[index] & mask);
        }
        return new IPAddress(bytes);
    }

    private static IPAddress SubnetBroadcastAddress(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        for (var index = 0; index < bytes.Length; index++)
        {
            var bits = Math.Clamp(prefixLength - index * 8, 0, 8);
            var mask = bits == 0 ? 0 : (0xFF << (8 - bits)) & 0xFF;
            bytes[index] = (byte)((bytes[index] & mask) | (~mask & 0xFF));
        }
        return new IPAddress(bytes);
    }
}

sealed record YouTubeSuggestion(string VideoId, string Title, string Thumbnail);

sealed class YouTubeBridge
{
    private static readonly Regex IdPattern = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly object gate = new();
    private List<YouTubeSuggestion> suggestions = [];
    private DateTimeOffset updatedAt = DateTimeOffset.MinValue;
    private string? command;

    public static bool ValidVideoId(string? value) => value is not null && IdPattern.IsMatch(value);
    public bool IsFresh { get { lock (gate) return suggestions.Count > 0 && DateTimeOffset.UtcNow - updatedAt < TimeSpan.FromSeconds(8); } }
    public void Update(List<YouTubeSuggestion> items) { lock (gate) { suggestions = items.Take(9).ToList(); updatedAt = DateTimeOffset.UtcNow; } }
    public IReadOnlyList<YouTubeSuggestion> Snapshot() { lock (gate) return IsFresh ? suggestions.ToArray() : []; }
    public bool Queue(string videoId) { lock (gate) { if (!IsFresh || !suggestions.Any(item => item.VideoId == videoId)) return false; command = videoId; return true; } }
    public string? TakeCommand() { lock (gate) { var result = command; command = null; return result; } }
}

sealed record MediaChapter(long PositionMs, string Title);

sealed record NvidiaReplaySnapshot(bool Available, bool Enabled, int BufferSeconds, int[] SaveKeys, int[] ToggleKeys, int[] ScreenshotKeys)
{
    public static NvidiaReplaySnapshot Unavailable { get; } = new(false, false, 120, [], [], []);
}

static class NvidiaReplayState
{
    private static readonly object Gate = new();
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NVIDIA Corporation", "NVIDIA Overlay", "ShareSettings.json");
    private static NvidiaReplaySnapshot lastGood = NvidiaReplaySnapshot.Unavailable;

    public static NvidiaReplaySnapshot Read()
    {
        lock (Gate)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                var settings = document.RootElement.GetProperty("settings");
                var shortcuts = settings.GetProperty("shortcuts");
                var saveKeys = ReadShortcut(shortcuts, "DVRSave");
                var toggleKeys = ReadShortcut(shortcuts, "DVRToggle");
                var screenshotKeys = ReadShortcut(shortcuts, "Screenshot");
                if (saveKeys.Length == 0 || toggleKeys.Length == 0) return lastGood;
                var video = settings.GetProperty("video");
                var enabled = video.TryGetProperty("irEnabled", out var enabledNode) && enabledNode.ValueKind == JsonValueKind.True;
                var seconds = video.TryGetProperty("irBufferLength", out var secondsNode) && secondsNode.TryGetInt32(out var value)
                    ? Math.Clamp(value, 15, 1200)
                    : 120;
                lastGood = new(true, enabled, seconds, saveKeys, toggleKeys, screenshotKeys);
                return lastGood;
            }
            catch
            {
                return lastGood;
            }
        }
    }

    private static int[] ReadShortcut(JsonElement shortcuts, string name)
    {
        if (!shortcuts.TryGetProperty(name, out var shortcut) || shortcut.ValueKind != JsonValueKind.Array) return [];
        return shortcut.EnumerateArray()
            .Take(4)
            .Select(node => node.TryGetInt32(out var key) ? key : 0)
            .Where(key => key is >= 8 and <= 254)
            .Distinct()
            .ToArray();
    }
}

sealed class YouTubeChapterProvider
{
    private static readonly Regex PlayerResponsePattern = new(
        @"var ytInitialPlayerResponse\s*=\s*(?<json>\{.+?\});</script>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex DescriptionPattern = new(
        "\"shortDescription\"\\s*:\\s*(?<value>\"(?:\\\\.|[^\"\\\\])*\")",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ChapterLinePattern = new(
        @"(?m)^[ \t]*(?<time>(?:\d{1,2}:)?\d{1,3}:\d{2})[ \t]*(?:[-–—|:][ \t]*)?(?<title>[^\r\n]{1,160})[ \t]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<MediaChapter>>> cache = new();
    private readonly HttpClient client = CreateClient();

    public async Task<IReadOnlyList<MediaChapter>> GetAsync(string source, string mediaTitle, long durationMs, CancellationToken cancellationToken)
    {
        if (!BrowserYouTube.TryCurrentVideoId(source, mediaTitle, out var videoId)) return [];
        if (cache.Count > 32) cache.Clear();
        var chapters = cache.GetOrAdd(videoId, id => FetchAsync(id, durationMs));
        if (!chapters.IsCompleted) return [];
        try { return await chapters.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
    }

    private async Task<IReadOnlyList<MediaChapter>> FetchAsync(string videoId, long durationMs)
    {
        try
        {
            var html = await client.GetStringAsync($"https://www.youtube.com/watch?v={videoId}&hl=en");
            var description = ReadDescription(html);
            if (string.IsNullOrWhiteSpace(description)) return [];
            var maximum = durationMs > 0 ? durationMs + 5000 : TimeSpan.FromHours(24).TotalMilliseconds;
            var chapters = ChapterLinePattern.Matches(description)
                .Select(match => (Position: ParseTimestamp(match.Groups["time"].Value), Title: CleanTitle(match.Groups["title"].Value)))
                .Where(item => item.Position is >= 0 && item.Position <= maximum && item.Title.Length > 0)
                .GroupBy(item => item.Position)
                .Select(group => new MediaChapter(group.Key, group.First().Title))
                .OrderBy(chapter => chapter.PositionMs)
                .Take(40)
                .ToArray();
            return chapters.Length >= 2 ? chapters : [];
        }
        catch
        {
            return [];
        }
    }

    private static string ReadDescription(string html)
    {
        var player = PlayerResponsePattern.Match(html);
        if (player.Success)
        {
            try
            {
                using var document = JsonDocument.Parse(player.Groups["json"].Value);
                if (document.RootElement.TryGetProperty("videoDetails", out var details) &&
                    details.TryGetProperty("shortDescription", out var description))
                    return description.GetString() ?? "";
            }
            catch (JsonException) { }
        }
        var fallback = DescriptionPattern.Match(html);
        if (!fallback.Success) return "";
        try { return JsonSerializer.Deserialize<string>(fallback.Groups["value"].Value) ?? ""; }
        catch (JsonException) { return ""; }
    }

    private static long ParseTimestamp(string value)
    {
        var parts = value.Split(':');
        if (parts.Length is not (2 or 3) || parts.Any(part => !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))) return -1;
        var numbers = parts.Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        if (numbers[^1] >= 60 || (numbers.Length == 3 && numbers[1] >= 60)) return -1;
        var seconds = numbers.Length == 2 ? numbers[0] * 60L + numbers[1] : numbers[0] * 3600L + numbers[1] * 60L + numbers[2];
        return seconds * 1000;
    }

    private static string CleanTitle(string value)
    {
        var title = Regex.Replace(value.Trim(), @"\s+", " ");
        return title[..Math.Min(title.Length, 120)];
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var result = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        result.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/145 Safari/537.36");
        result.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return result;
    }
}

static class BrowserYouTube
{
    public static bool TryCurrentVideoId(string source, string mediaTitle, out string videoId)
    {
        videoId = "";
        var processName = ProcessName(source);
        if (processName is null) return false;
        var processes = Process.GetProcessesByName(processName);
        try
        {
            var windows = processes
                .Select(process =>
                {
                    try { process.Refresh(); return (Window: process.MainWindowHandle, Title: process.MainWindowTitle); }
                    catch { return (Window: IntPtr.Zero, Title: ""); }
                })
                .Where(item => item.Window != IntPtr.Zero)
                .ToArray();
            var matches = windows.Where(item => !string.IsNullOrWhiteSpace(mediaTitle) && item.Title.Contains(mediaTitle, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) return false;
            var selected = matches[0];
            var root = AutomationElement.FromHandle(selected.Window);
            if (root is null) return false;
            var addressBar = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "view_1012"));
            if (addressBar is null) return false;
            try
            {
                if (addressBar.GetCurrentPattern(ValuePattern.Pattern) is ValuePattern value)
                    return TryParseVideoId(value.Current.Value, out videoId);
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
            return false;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static bool TryParseVideoId(string address, out string videoId)
    {
        videoId = "";
        if (string.IsNullOrWhiteSpace(address)) return false;
        if (!address.Contains("://", StringComparison.Ordinal)) address = "https://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return false;
        if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = uri.AbsolutePath.Trim('/').Split('/')[0];
            if (YouTubeBridge.ValidVideoId(candidate)) { videoId = candidate; return true; }
            return false;
        }
        if (!uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            if (pair.Length != 2 || !pair[0].Equals("v", StringComparison.OrdinalIgnoreCase)) continue;
            var candidate = Uri.UnescapeDataString(pair[1]);
            if (YouTubeBridge.ValidVideoId(candidate)) { videoId = candidate; return true; }
        }
        return false;
    }

    private static string? ProcessName(string source)
    {
        if (source.Contains("Brave", StringComparison.OrdinalIgnoreCase)) return "brave";
        if (source.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "chrome";
        if (source.Contains("Edge", StringComparison.OrdinalIgnoreCase)) return "msedge";
        return null;
    }
}

sealed record YouTubeActionResult(bool Success, string Action, string Message)
{
    public static YouTubeActionResult Failed(string message) => new(false, "", message);
}

sealed record YouTubeVolumeResult(bool Success, int Volume, string Message)
{
    public static YouTubeVolumeResult Failed(string message) => new(false, -1, message);
}

static class YouTubeActions
{
    private const uint WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmMouseMove = 0x0200, WmLeftButtonDown = 0x0201, WmLeftButtonUp = 0x0202;
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    public static YouTubeActionResult Invoke(string source, string mediaTitle, string action)
    {
        if (action is not ("like" or "dislike" or "subscribe"))
            return YouTubeActionResult.Failed("That YouTube action is not allowed.");
        if (!TrySelectedYouTube(source, mediaTitle, out var root, out var uri, out var selectionError))
            return YouTubeActionResult.Failed(selectionError);
        try
        {
            var music = uri.Host.Equals("music.youtube.com", StringComparison.OrdinalIgnoreCase);
            if (action == "subscribe" && music)
                return YouTubeActionResult.Failed("Subscribe is not exposed by YouTube Music; open the video on YouTube to subscribe.");

            var buttons = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            AutomationElement? target = null;
            var alreadySubscribed = false;
            foreach (AutomationElement button in buttons)
            {
                try
                {
                    if (!button.Current.IsEnabled || button.Current.IsOffscreen) continue;
                    var name = button.Current.Name?.Trim() ?? "";
                    if (action == "subscribe" && IsSubscribedLabel(name)) alreadySubscribed = true;
                    if (Matches(name, action, music)) { target = button; break; }
                }
                catch (ElementNotAvailableException) { }
            }
            if (target is null)
            {
                if (action == "subscribe" && alreadySubscribed)
                    return new(true, "already-subscribed", "Already subscribed to this YouTube channel");
                return YouTubeActionResult.Failed($"The visible YouTube {DisplayName(action)} button was not found.");
            }

            try
            {
                if (target.TryGetCurrentPattern(TogglePattern.Pattern, out var toggleObject) && toggleObject is TogglePattern toggle)
                {
                    var removing = toggle.Current.ToggleState == ToggleState.On;
                    toggle.Toggle();
                    var message = action switch
                    {
                        "like" => removing ? "YouTube Like removed" : "Liked on YouTube",
                        "dislike" => removing ? "YouTube Dislike removed" : "Disliked on YouTube",
                        _ => "Subscribed on YouTube"
                    };
                    return new(true, removing ? $"{action}-removed" : action, message);
                }
                if (target.TryGetCurrentPattern(InvokePattern.Pattern, out var invokeObject) && invokeObject is InvokePattern invoke)
                {
                    invoke.Invoke();
                    return new(true, action, action == "subscribe" ? "Subscribed on YouTube" : $"YouTube {DisplayName(action)} toggled");
                }
                return YouTubeActionResult.Failed($"YouTube's {DisplayName(action)} control is visible but Windows cannot activate it.");
            }
            catch (ElementNotAvailableException) { return YouTubeActionResult.Failed("The YouTube page changed while the action was being sent; try again."); }
            catch (InvalidOperationException) { return YouTubeActionResult.Failed($"Windows could not activate YouTube's {DisplayName(action)} control."); }
        }
        catch (COMException)
        {
            return YouTubeActionResult.Failed("Windows accessibility could not inspect the selected YouTube window; show it once and try again.");
        }
        catch (InvalidOperationException)
        {
            return YouTubeActionResult.Failed("The selected YouTube window changed while the action was being sent; try again.");
        }
    }

    public static int ReadVolume(string source, string mediaTitle)
    {
        if (!TrySelectedYouTube(source, mediaTitle, out var root, out _, out _)) return -1;
        try
        {
            var slider = FindVolumeSlider(root);
            if (slider is null || !slider.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rangeObject) || rangeObject is not RangeValuePattern range)
                return -1;
            return NormalizeVolume(range.Current.Value, range.Current.Minimum, range.Current.Maximum);
        }
        catch (ElementNotAvailableException) { return -1; }
        catch (InvalidOperationException) { return -1; }
        catch (COMException) { return -1; }
    }

    public static YouTubeVolumeResult SetVolume(string source, string mediaTitle, int level)
    {
        if (level is < 0 or > 100) return YouTubeVolumeResult.Failed("YouTube volume must be from 0 to 100.");
        if (!TrySelectedYouTube(source, mediaTitle, out var root, out _, out var selectionError))
            return YouTubeVolumeResult.Failed(selectionError);
        try
        {
            var slider = FindVolumeSlider(root);
            if (slider is null)
                return YouTubeVolumeResult.Failed("The selected YouTube player's Volume slider was not found.");
            if (!slider.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rangeObject) || rangeObject is not RangeValuePattern range || range.Current.IsReadOnly)
                return YouTubeVolumeResult.Failed("YouTube's Volume slider is visible but Windows cannot change it.");
            var minimum = range.Current.Minimum;
            var maximum = range.Current.Maximum;
            range.SetValue(minimum + (maximum - minimum) * level / 100d);
            Thread.Sleep(75);
            var current = ReadVolume(source, mediaTitle);
            if (current < 0) return YouTubeVolumeResult.Failed("YouTube's Volume slider disappeared while it was being changed.");
            if (Math.Abs(current - level) <= 2) return new(true, current, $"YouTube volume {current}%");
            if (!PostSliderLevel(slider, current, level))
                return YouTubeVolumeResult.Failed("Windows could not send the volume change to the background YouTube player.");
            for (var attempt = 0; attempt < 8; attempt++)
            {
                Thread.Sleep(75);
                var observed = ReadVolume(source, mediaTitle);
                if (observed < 0) continue;
                if (Math.Abs(observed - level) <= 2) return new(true, observed, $"YouTube volume {observed}%");
            }
            return YouTubeVolumeResult.Failed("The background YouTube player ignored the volume change.");
        }
        catch (ElementNotAvailableException) { return YouTubeVolumeResult.Failed("The YouTube page changed while volume was being set; try again."); }
        catch (InvalidOperationException) { return YouTubeVolumeResult.Failed("Windows could not change YouTube's Volume slider."); }
        catch (COMException) { return YouTubeVolumeResult.Failed("Windows accessibility could not change YouTube volume; show the player once and try again."); }
    }

    private static AutomationElement? FindVolumeSlider(AutomationElement root)
    {
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Slider),
            new PropertyCondition(AutomationElement.NameProperty, "Volume"));
        var sliders = root.FindAll(TreeScope.Descendants, condition);
        foreach (AutomationElement slider in sliders)
        {
            try { if (slider.Current.IsEnabled) return slider; }
            catch (ElementNotAvailableException) { }
        }
        return null;
    }

    private static int NormalizeVolume(double value, double minimum, double maximum)
    {
        if (maximum <= minimum) return -1;
        return Math.Clamp((int)Math.Round((value - minimum) * 100d / (maximum - minimum)), 0, 100);
    }

    private static bool PostSliderLevel(AutomationElement slider, int currentLevel, int targetLevel)
    {
        var window = IntPtr.Zero;
        var current = slider;
        var walker = TreeWalker.RawViewWalker;
        for (var depth = 0; depth < 20 && current is not null; depth++)
        {
            try
            {
                if (current.Current.ClassName.Equals("Chrome_RenderWidgetHostHWND", StringComparison.Ordinal) && current.Current.NativeWindowHandle != 0)
                {
                    window = (IntPtr)current.Current.NativeWindowHandle;
                    break;
                }
                current = walker.GetParent(current);
            }
            catch (ElementNotAvailableException) { return false; }
        }
        var bounds = slider.Current.BoundingRectangle;
        if (window == IntPtr.Zero || bounds.Width < 16 || bounds.Height < 8) return false;
        const int endpointInset = 4;
        var point = new NativePoint
        {
            X = (int)Math.Round(bounds.Left + endpointInset + (bounds.Width - endpointInset * 2) * currentLevel / 100d),
            Y = (int)Math.Round(bounds.Top + bounds.Height / 2d)
        };
        if (!ScreenToClient(window, ref point)) return false;
        var packed = (IntPtr)(((point.Y & 0xffff) << 16) | (point.X & 0xffff));
        if (!PostMessage(window, WmMouseMove, UIntPtr.Zero, packed) ||
            !PostMessage(window, WmLeftButtonDown, (UIntPtr)1, packed) ||
            !PostMessage(window, WmLeftButtonUp, UIntPtr.Zero, packed)) return false;
        var key = targetLevel >= currentLevel ? 0x27u : 0x25u;
        var keyUpFlags = (IntPtr)unchecked((int)0xC0000001u);
        for (var step = 0; step < Math.Abs(targetLevel - currentLevel); step++)
        {
            if (!PostMessage(window, WmKeyDown, (UIntPtr)key, (IntPtr)1) ||
                !PostMessage(window, WmKeyUp, (UIntPtr)key, keyUpFlags)) return false;
        }
        return true;
    }

    private static bool TrySelectedYouTube(string source, string mediaTitle, out AutomationElement root, out Uri uri, out string error)
    {
        root = null!;
        uri = null!;
        error = "";
        var processName = ProcessName(source);
        if (processName is null)
        {
            error = "The selected player is not a supported YouTube browser session.";
            return false;
        }
        var processes = Process.GetProcessesByName(processName);
        try
        {
            var windows = processes
                .Select(process =>
                {
                    try { process.Refresh(); return (Window: process.MainWindowHandle, Title: process.MainWindowTitle); }
                    catch { return (Window: IntPtr.Zero, Title: ""); }
                })
                .Where(item => item.Window != IntPtr.Zero)
                .ToArray();
            var matches = windows
                .Where(item => !string.IsNullOrWhiteSpace(mediaTitle) && item.Title.Contains(mediaTitle, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var selected = matches.Length == 1 ? matches[0] : windows.Length == 1 ? windows[0] : default;
            if (selected.Window == IntPtr.Zero)
            {
                error = "More than one browser window is open; show the intended YouTube tab once and try again.";
                return false;
            }
            var selectedRoot = AutomationElement.FromHandle(selected.Window);
            if (selectedRoot is null)
            {
                error = "Windows could not inspect the selected browser window.";
                return false;
            }
            if (!TryReadAddress(selectedRoot, out var address) || !Uri.TryCreate(address, UriKind.Absolute, out var selectedUri) || !IsYouTube(selectedUri))
            {
                error = "The selected browser window is not on YouTube.";
                return false;
            }
            root = selectedRoot;
            uri = selectedUri;
            return true;
        }
        catch (COMException)
        {
            error = "Windows accessibility could not inspect the selected YouTube window; show it once and try again.";
            return false;
        }
        catch (InvalidOperationException)
        {
            error = "The selected YouTube window changed while it was being inspected; try again.";
            return false;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static bool TryReadAddress(AutomationElement root, out string address)
    {
        address = "";
        var addressBar = root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "view_1012"));
        if (addressBar is null) return false;
        try
        {
            if (addressBar.GetCurrentPattern(ValuePattern.Pattern) is not ValuePattern value) return false;
            address = value.Current.Value;
            if (!address.Contains("://", StringComparison.Ordinal)) address = "https://" + address;
            return true;
        }
        catch (ElementNotAvailableException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool IsYouTube(Uri uri) =>
        uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);

    private static bool Matches(string label, string action, bool music) => action switch
    {
        "like" => music
            ? label.Equals("Like", StringComparison.OrdinalIgnoreCase)
            : label.StartsWith("like this video", StringComparison.OrdinalIgnoreCase),
        "dislike" => music
            ? label.Equals("Dislike", StringComparison.OrdinalIgnoreCase)
            : label.StartsWith("dislike this video", StringComparison.OrdinalIgnoreCase),
        "subscribe" => label.Equals("Subscribe", StringComparison.OrdinalIgnoreCase) ||
            label.StartsWith("Subscribe to ", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static bool IsSubscribedLabel(string label) =>
        label.Equals("Subscribed", StringComparison.OrdinalIgnoreCase) ||
        label.Equals("Unsubscribe", StringComparison.OrdinalIgnoreCase) ||
        label.StartsWith("Unsubscribe from ", StringComparison.OrdinalIgnoreCase);

    private static string DisplayName(string action) => action switch
    {
        "like" => "Like",
        "dislike" => "Dislike",
        _ => "Subscribe"
    };

    private static string? ProcessName(string source)
    {
        if (source.Contains("Brave", StringComparison.OrdinalIgnoreCase)) return "brave";
        if (source.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "chrome";
        if (source.Contains("Edge", StringComparison.OrdinalIgnoreCase)) return "msedge";
        return null;
    }
}

static class TrayApplication
{
    public static void Start(SecurityState security, IHostApplicationLifetime lifetime, LanAccessPolicy lanAccess)
    {
        var thread = new Thread(() =>
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            System.Windows.Forms.Application.Run(new TrayContext(security, lifetime, lanAccess));
        }) { IsBackground = true, Name = "MASHR Media Deck tray" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private sealed class TrayContext : System.Windows.Forms.ApplicationContext
    {
        private readonly SecurityState security;
        private readonly IHostApplicationLifetime lifetime;
        private readonly LanAccessPolicy lanAccess;
        private readonly System.Windows.Forms.NotifyIcon icon;
        private readonly PairingDashboard dashboard;
        private readonly System.Windows.Forms.Timer statusTimer;

        public TrayContext(SecurityState security, IHostApplicationLifetime lifetime, LanAccessPolicy lanAccess)
        {
            this.security = security;
            this.lifetime = lifetime;
            this.lanAccess = lanAccess;
            var menu = new System.Windows.Forms.ContextMenuStrip();
            var openItem = menu.Items.Add("Open Media Deck", null, (_, _) => ShowDashboard());
            openItem.Font = new Font(openItem.Font, FontStyle.Bold);
            menu.Items.Add("Start pairing mode (2 min)", null, (_, _) => StartPairing());
            menu.Items.Add("Copy pairing code", null, (_, _) => CopyPairingCode());
            menu.Items.Add("Manage paired devices", null, (_, _) => ShowDashboard());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Exit MASHR Media Deck", null, (_, _) => Exit());
            icon = new System.Windows.Forms.NotifyIcon
            {
                Icon = SystemIcons.Shield,
                Text = "MASHR Media Deck Companion",
                ContextMenuStrip = menu,
                Visible = true
            };
            icon.DoubleClick += (_, _) => ShowDashboard();
            dashboard = new PairingDashboard(security, lanAccess, Exit);
            statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            statusTimer.Tick += (_, _) => RefreshTrayStatus();
            statusTimer.Start();
            RefreshTrayStatus();
            dashboard.Show();
        }

        private void ShowDashboard()
        {
            dashboard.BringUp();
        }

        private void StartPairing()
        {
            security.StartPairing();
            dashboard.BringUp();
            icon.BalloonTipTitle = "Pairing mode is open";
            icon.BalloonTipText = $"Enter {security.PairingCode} on each phone within two minutes.";
            icon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
            icon.ShowBalloonTip(5000);
        }

        private void CopyPairingCode()
        {
            if (!security.IsPairingOpen) { StartPairing(); return; }
            try { System.Windows.Forms.Clipboard.SetText(security.PairingCode); } catch { }
            ShowDashboard();
        }

        private void RefreshTrayStatus()
        {
            var count = security.DeviceCount;
            icon.Text = $"MASHR Media Deck • {count} device{(count == 1 ? "" : "s")}";
        }

        private void Exit()
        {
            statusTimer.Stop();
            dashboard.AllowClose();
            dashboard.Close();
            icon.Visible = false;
            icon.Dispose();
            lifetime.StopApplication();
            ExitThread();
        }
    }

    private sealed class PairingDashboard : System.Windows.Forms.Form
    {
        private static readonly Color Background = Color.FromArgb(10, 10, 17);
        private static readonly Color Card = Color.FromArgb(25, 25, 38);
        private static readonly Color CardRaised = Color.FromArgb(47, 43, 67);
        private static readonly Color Ink = Color.FromArgb(247, 245, 255);
        private static readonly Color Muted = Color.FromArgb(180, 176, 199);
        private static readonly Color Purple = Color.FromArgb(167, 139, 250);
        private static readonly Color Gold = Color.FromArgb(202, 145, 0);
        private static readonly Color Green = Color.FromArgb(74, 222, 128);
        private readonly SecurityState security;
        private readonly LanAccessPolicy lanAccess;
        private readonly Action exit;
        private readonly System.Windows.Forms.Label modeLabel;
        private readonly System.Windows.Forms.Label codeLabel;
        private readonly System.Windows.Forms.Label timerLabel;
        private readonly System.Windows.Forms.Label networkLabel;
        private readonly System.Windows.Forms.Label nearbyHint;
        private readonly System.Windows.Forms.Label deviceHeading;
        private readonly System.Windows.Forms.ComboBox nearbyList;
        private readonly System.Windows.Forms.ListView deviceList;
        private readonly System.Windows.Forms.Button approveNearbyButton;
        private readonly System.Windows.Forms.Button startButton;
        private readonly System.Windows.Forms.Button stopButton;
        private readonly System.Windows.Forms.Button copyButton;
        private readonly System.Windows.Forms.Button revokeButton;
        private readonly System.Windows.Forms.Timer refreshTimer;
        private bool canClose;

        public PairingDashboard(SecurityState security, LanAccessPolicy lanAccess, Action exit)
        {
            this.security = security;
            this.lanAccess = lanAccess;
            this.exit = exit;
            Text = "MASHR Media Deck";
            Icon = SystemIcons.Shield;
            BackColor = Background;
            ForeColor = Ink;
            Font = new Font("Segoe UI", 10f);
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            MinimumSize = new Size(760, 690);
            ClientSize = new Size(820, 742);
            ShowInTaskbar = true;

            var header = new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Top, Height = 104, Padding = new System.Windows.Forms.Padding(24, 17, 24, 10) };
            var brand = new System.Windows.Forms.Label
            {
                Text = "M A S H R",
                Font = new Font("Segoe UI", 25f, FontStyle.Bold),
                ForeColor = Ink,
                AutoSize = true,
                Location = new Point(22, 12)
            };
            var product = new System.Windows.Forms.Label
            {
                Text = "MEDIA DECK  •  PC COMPANION",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = Purple,
                AutoSize = true,
                Location = new Point(25, 61)
            };
            networkLabel = new System.Windows.Forms.Label
            {
                AutoSize = false,
                Height = 48,
                Width = 390,
                Dock = System.Windows.Forms.DockStyle.Right,
                Padding = new System.Windows.Forms.Padding(0, 0, 22, 0),
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = Muted
            };
            header.Controls.Add(brand);
            header.Controls.Add(product);
            header.Controls.Add(networkLabel);

            var pairingCard = new System.Windows.Forms.Panel
            {
                Dock = System.Windows.Forms.DockStyle.Top,
                Height = 270,
                BackColor = Card,
                Padding = new System.Windows.Forms.Padding(22, 15, 22, 15)
            };
            var nearbyHeading = new System.Windows.Forms.Label
            {
                Text = "NEARBY CONTROLLERS  •  OPEN THE APP ON A PHONE",
                AutoSize = true,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = Ink,
                Location = new Point(22, 13)
            };
            nearbyList = new System.Windows.Forms.ComboBox
            {
                Location = new Point(22, 42),
                Width = 492,
                Height = 36,
                DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList,
                BackColor = CardRaised,
                ForeColor = Ink,
                FlatStyle = System.Windows.Forms.FlatStyle.Flat,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold)
            };
            nearbyList.SelectedIndexChanged += (_, _) => RefreshNearbyMessage();
            approveNearbyButton = DeckButton("PAIR THIS DEVICE", Green, Color.FromArgb(7, 35, 18), 270);
            approveNearbyButton.Location = new Point(528, 36);
            approveNearbyButton.Enabled = false;
            approveNearbyButton.Click += (_, _) => PairNearby();
            nearbyHint = new System.Windows.Forms.Label
            {
                Text = "No unpaired phones seen yet. Open MASHR Media Deck on the phone.",
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = Muted,
                Location = new Point(24, 87)
            };
            var divider = new System.Windows.Forms.Panel
            {
                BackColor = CardRaised,
                Location = new Point(22, 111),
                Size = new Size(776, 1)
            };
            modeLabel = new System.Windows.Forms.Label
            {
                Text = "FALLBACK CODE CLOSED",
                AutoSize = true,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = Purple,
                Location = new Point(22, 124)
            };
            codeLabel = new System.Windows.Forms.Label
            {
                Text = "— — —",
                AutoSize = true,
                Font = new Font("Consolas", 31f, FontStyle.Bold),
                ForeColor = Ink,
                Location = new Point(18, 149)
            };
            timerLabel = new System.Windows.Forms.Label
            {
                Text = "Only needed if nearby discovery is unavailable.",
                AutoSize = true,
                Font = new Font("Segoe UI", 10f),
                ForeColor = Muted,
                Location = new Point(24, 207)
            };
            startButton = DeckButton("START CODE MODE", Purple, Color.FromArgb(19, 13, 35), 154);
            startButton.Location = new Point(420, 136);
            startButton.Click += (_, _) => { security.StartPairing(); RefreshState(); };
            copyButton = DeckButton("COPY CODE", CardRaised, Ink, 132);
            copyButton.Location = new Point(584, 136);
            copyButton.Click += (_, _) => CopyCode();
            stopButton = DeckButton("STOP", Color.FromArgb(92, 45, 45), Ink, 132);
            stopButton.Location = new Point(584, 192);
            stopButton.Click += (_, _) => { security.StopPairing(); RefreshState(); };
            var howLabel = new System.Windows.Forms.Label
            {
                Text = "FALLBACK: PC SETTINGS ON PHONE  →  ENTER THIS CODE  →  PAIR",
                AutoSize = true,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Purple,
                Location = new Point(24, 240)
            };
            pairingCard.Controls.AddRange([nearbyHeading, nearbyList, approveNearbyButton, nearbyHint, divider, modeLabel, codeLabel, timerLabel, startButton, copyButton, stopButton, howLabel]);

            var devicePanel = new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Fill, Padding = new System.Windows.Forms.Padding(22, 18, 22, 12) };
            deviceHeading = new System.Windows.Forms.Label
            {
                Text = "PAIRED CONTROLLERS",
                Dock = System.Windows.Forms.DockStyle.Top,
                Height = 34,
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = Ink
            };
            deviceList = new System.Windows.Forms.ListView
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                View = System.Windows.Forms.View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                BackColor = Card,
                ForeColor = Ink,
                BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 10f)
            };
            deviceList.Columns.Add("DEVICE", 210);
            deviceList.Columns.Add("STATUS", 90);
            deviceList.Columns.Add("ADDRESS", 135);
            deviceList.Columns.Add("PAIRED", 130);
            deviceList.Columns.Add("LAST SEEN", 135);
            var actions = new System.Windows.Forms.FlowLayoutPanel
            {
                Dock = System.Windows.Forms.DockStyle.Bottom,
                Height = 62,
                FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight,
                Padding = new System.Windows.Forms.Padding(0, 10, 0, 0),
                BackColor = Background
            };
            revokeButton = DeckButton("REVOKE SELECTED", Color.FromArgb(92, 45, 45), Ink, 172);
            revokeButton.Enabled = false;
            revokeButton.Click += (_, _) => RevokeSelected();
            deviceList.SelectedIndexChanged += (_, _) => revokeButton.Enabled = deviceList.SelectedItems.Count == 1;
            var forgetButton = DeckButton("FORGET ALL", CardRaised, Ink, 136);
            forgetButton.Click += (_, _) => ForgetAll();
            var hideButton = DeckButton("HIDE TO TRAY", CardRaised, Ink, 140);
            hideButton.Click += (_, _) => Hide();
            var exitButton = DeckButton("EXIT", CardRaised, Ink, 96);
            exitButton.Click += (_, _) => exit();
            actions.Controls.AddRange([revokeButton, forgetButton, hideButton, exitButton]);
            devicePanel.Controls.Add(deviceList);
            devicePanel.Controls.Add(deviceHeading);
            devicePanel.Controls.Add(actions);

            Controls.Add(devicePanel);
            Controls.Add(pairingCard);
            Controls.Add(header);
            FormClosing += OnFormClosing;
            Shown += (_, _) => CenterOnPrimaryDisplay();
            refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            refreshTimer.Tick += (_, _) => RefreshState();
            refreshTimer.Start();
            RefreshState();
        }

        public void BringUp()
        {
            Show();
            if (WindowState == System.Windows.Forms.FormWindowState.Minimized) WindowState = System.Windows.Forms.FormWindowState.Normal;
            EnsureVisible();
            Activate();
            BringToFront();
            RefreshState();
        }

        public void AllowClose() { canClose = true; refreshTimer.Stop(); }

        private void RefreshState()
        {
            var open = security.IsPairingOpen;
            var seconds = security.PairingSecondsRemaining;
            modeLabel.Text = open ? "FALLBACK CODE OPEN" : "FALLBACK CODE CLOSED";
            modeLabel.ForeColor = open ? Gold : Purple;
            codeLabel.Text = open ? FormatCode(security.PairingCode) : "— — —";
            timerLabel.Text = open
                ? $"Open for {seconds / 60}:{seconds % 60:00}. You can pair several phones during this window."
                : "Only needed if nearby discovery is unavailable.";
            networkLabel.Text = lanAccess.IsEnabled
                ? $"LAN GATE  •  {lanAccess.Display.ToUpperInvariant()}\nSIGNED COMMANDS ONLY"
                : "LAN ACCESS OFF\nLOOPBACK ONLY";
            networkLabel.ForeColor = lanAccess.IsEnabled ? Green : Color.FromArgb(248, 113, 113);
            RefreshNearby();
            RefreshDevices();
        }

        private void RefreshNearby()
        {
            var selected = nearbyList.SelectedItem as NearbyChoice;
            var snapshot = security.SnapshotNearby();
            nearbyList.BeginUpdate();
            nearbyList.Items.Clear();
            foreach (var request in snapshot)
                nearbyList.Items.Add(new NearbyChoice(request.RequestId, request.Name, request.Address, request.ExpiresAtUtc, request.Approved));
            if (nearbyList.Items.Count > 0)
            {
                var selectedIndex = 0;
                if (selected is not null)
                    for (var index = 0; index < nearbyList.Items.Count; index++)
                        if ((nearbyList.Items[index] as NearbyChoice)?.RequestId == selected.RequestId) { selectedIndex = index; break; }
                nearbyList.SelectedIndex = selectedIndex;
            }
            nearbyList.EndUpdate();
            nearbyList.Enabled = nearbyList.Items.Count > 0;
            RefreshNearbyMessage();
        }

        private void RefreshNearbyMessage()
        {
            var selected = nearbyList.SelectedItem as NearbyChoice;
            approveNearbyButton.Enabled = selected is not null && !selected.Approved;
            if (selected is null)
            {
                nearbyHint.Text = "No unpaired phones seen yet. Open MASHR Media Deck on the phone.";
                nearbyHint.ForeColor = Muted;
                return;
            }
            if (selected.Approved)
            {
                var remaining = Math.Max(0, (int)Math.Ceiling((selected.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds));
                nearbyHint.Text = $"Approved for {remaining}s. Waiting for that exact phone and IP to collect its one-use key...";
                nearbyHint.ForeColor = Gold;
                return;
            }
            nearbyHint.Text = $"{selected.Name} at {selected.Address} is asking. Confirm it is yours, then click once.";
            nearbyHint.ForeColor = Green;
        }

        private void PairNearby()
        {
            if (nearbyList.SelectedItem is not NearbyChoice selected) return;
            security.ApproveNearby(selected.RequestId);
            RefreshState();
        }

        private void RefreshDevices()
        {
            var selected = deviceList.SelectedItems.Count == 1 ? deviceList.SelectedItems[0].Tag as string : null;
            var snapshot = security.Snapshot();
            deviceHeading.Text = $"PAIRED CONTROLLERS  •  {snapshot.Count}";
            deviceList.BeginUpdate();
            deviceList.Items.Clear();
            foreach (var device in snapshot)
            {
                var item = new System.Windows.Forms.ListViewItem(device.Name) { Tag = device.Id };
                item.SubItems.Add(device.Online ? "ONLINE" : "READY");
                item.SubItems.Add(string.IsNullOrWhiteSpace(device.Address) ? "—" : device.Address);
                item.SubItems.Add(device.PairedAtUtc.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.CurrentCulture));
                item.SubItems.Add(device.LastSeenUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture));
                item.ForeColor = device.Online ? Green : Ink;
                deviceList.Items.Add(item);
                if (device.Id == selected) item.Selected = true;
            }
            deviceList.EndUpdate();
            revokeButton.Enabled = deviceList.SelectedItems.Count == 1;
        }

        private void CopyCode()
        {
            if (!security.IsPairingOpen) return;
            try { System.Windows.Forms.Clipboard.SetText(security.PairingCode); } catch { }
            timerLabel.Text = "Code copied. Enter it on any phone while pairing mode stays open.";
        }

        private void RevokeSelected()
        {
            if (deviceList.SelectedItems.Count != 1) return;
            var item = deviceList.SelectedItems[0];
            var deviceId = item.Tag as string;
            if (deviceId is null) return;
            var answer = System.Windows.Forms.MessageBox.Show(this,
                $"Revoke {item.Text}? That phone will stop controlling this PC immediately.",
                "Revoke controller",
                System.Windows.Forms.MessageBoxButtons.OKCancel,
                System.Windows.Forms.MessageBoxIcon.Warning);
            if (answer == System.Windows.Forms.DialogResult.OK) security.Revoke(deviceId);
            RefreshState();
        }

        private void ForgetAll()
        {
            var answer = System.Windows.Forms.MessageBox.Show(this,
                "Forget every paired controller and open a fresh two-minute pairing window?",
                "Forget all controllers",
                System.Windows.Forms.MessageBoxButtons.OKCancel,
                System.Windows.Forms.MessageBoxIcon.Warning);
            if (answer != System.Windows.Forms.DialogResult.OK) return;
            security.RevokeAllAndPair();
            RefreshState();
        }

        private void OnFormClosing(object? sender, System.Windows.Forms.FormClosingEventArgs eventArgs)
        {
            if (canClose) return;
            eventArgs.Cancel = true;
            Hide();
        }

        private void EnsureVisible()
        {
            var visible = System.Windows.Forms.Screen.AllScreens.Any(screen => screen.WorkingArea.Contains(Bounds));
            if (!visible) CenterOnPrimaryDisplay();
        }

        private void CenterOnPrimaryDisplay()
        {
            var area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? System.Windows.Forms.Screen.FromControl(this).WorkingArea;
            Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2), area.Top + Math.Max(0, (area.Height - Height) / 2));
        }

        private static System.Windows.Forms.Button DeckButton(string text, Color background, Color foreground, int width)
        {
            return new System.Windows.Forms.Button
            {
                Text = text,
                Width = width,
                Height = 48,
                BackColor = background,
                ForeColor = foreground,
                FlatStyle = System.Windows.Forms.FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = System.Windows.Forms.Cursors.Hand,
                Margin = new System.Windows.Forms.Padding(0, 0, 10, 0),
                UseVisualStyleBackColor = false
            };
        }

        private sealed record NearbyChoice(string RequestId, string Name, string Address, DateTimeOffset ExpiresAtUtc, bool Approved)
        {
            public override string ToString() => $"{Name}  •  {Address}{(Approved ? "  •  APPROVED" : "")}";
        }

        private static string FormatCode(string code) => code.Length == 6 ? $"{code[..3]}  {code[3..]}" : code;
    }
}

static class MediaKeys
{
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    private static readonly object AltGate = new();
    private static bool altHeld;
    private static CancellationTokenSource? altTimeout;
    public static void Tap(int key) { keybd_event((byte)key, 0, 0, UIntPtr.Zero); keybd_event((byte)key, 0, 2, UIntPtr.Zero); }
    public static void AltTab() { keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x09, 0, 0, UIntPtr.Zero); keybd_event(0x09, 0, 2, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero); }
    public static void Hotkey(IReadOnlyList<int> keys)
    {
        if (keys.Count == 0) return;
        lock (AltGate)
        {
            if (altHeld) ReleaseAlt();
            for (var index = 0; index < keys.Count - 1; index++) keybd_event((byte)keys[index], 0, 0, UIntPtr.Zero);
            Tap(keys[^1]);
            for (var index = keys.Count - 2; index >= 0; index--) keybd_event((byte)keys[index], 0, 2, UIntPtr.Zero);
        }
    }
    public static void BeginAltTab() { lock (AltGate) { if (!altHeld) { keybd_event(0x12, 0, 0, UIntPtr.Zero); altHeld = true; } Tap(0x09); ArmAltTimeout(); } }
    public static void SwitcherArrow(int direction) { lock (AltGate) { if (!altHeld) return; Tap(direction < 0 ? 0x25 : 0x27); ArmAltTimeout(); } }
    public static void EndAltTab() { lock (AltGate) ReleaseAlt(); }
    private static void ArmAltTimeout() { altTimeout?.Cancel(); var token = (altTimeout = new CancellationTokenSource()).Token; _ = Task.Delay(TimeSpan.FromSeconds(10), token).ContinueWith(_ => { if (!token.IsCancellationRequested) lock (AltGate) ReleaseAlt(); }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default); }
    private static void ReleaseAlt() { altTimeout?.Cancel(); altTimeout = null; if (altHeld) { keybd_event(0x12, 0, 2, UIntPtr.Zero); altHeld = false; } }
}

static class TimelineClock
{
    public static long PositionMs(GlobalSystemMediaTransportControlsSessionPlaybackInfo playback, GlobalSystemMediaTransportControlsSessionTimelineProperties timeline) =>
        Math.Max(0, (PositionTicks(playback, timeline) - timeline.StartTime.Ticks) / TimeSpan.TicksPerMillisecond);

    public static long PositionTicks(GlobalSystemMediaTransportControlsSessionPlaybackInfo playback, GlobalSystemMediaTransportControlsSessionTimelineProperties timeline)
    {
        var position = timeline.Position.Ticks;
        if (playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            var elapsed = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
            var timelineSpan = timeline.MaxSeekTime - timeline.MinSeekTime;
            var maximumAge = timelineSpan > TimeSpan.Zero ? timelineSpan + TimeSpan.FromMinutes(5) : TimeSpan.FromDays(1);
            if (elapsed > TimeSpan.Zero && elapsed <= maximumAge)
            {
                var rate = playback.PlaybackRate is > 0 ? playback.PlaybackRate.Value : 1.0;
                var estimate = position + elapsed.Ticks * rate;
                position = estimate >= long.MaxValue ? long.MaxValue : estimate <= long.MinValue ? long.MinValue : (long)estimate;
            }
        }
        return Math.Clamp(position, timeline.MinSeekTime.Ticks, timeline.MaxSeekTime.Ticks);
    }
}

sealed record WindowMoveResult(bool Success, string Display, int Monitor, int MonitorCount, string Error)
{
    public static WindowMoveResult Failed(string error) => new(false, "", 0, 0, error);
}

static class WindowMover
{
    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpNoOwnerZOrder = 0x0200, SwpAsyncWindowPos = 0x4000;
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }

    public static WindowMoveResult MoveToNext(string source, string mediaTitle)
    {
        var processName = ProcessName(source);
        if (processName is null) return WindowMoveResult.Failed("This media player does not support screen switching yet.");
        var candidates = Process.GetProcessesByName(processName)
            .Select(process => { try { process.Refresh(); return (Process: process, Window: process.MainWindowHandle, Title: process.MainWindowTitle); } catch { return (Process: process, Window: IntPtr.Zero, Title: ""); } })
            .Where(item => item.Window != IntPtr.Zero)
            .ToArray();
        if (candidates.Length == 0) return WindowMoveResult.Failed("The selected media window is not open.");

        var matches = candidates.Where(item => !string.IsNullOrWhiteSpace(mediaTitle) && item.Title.Contains(mediaTitle, StringComparison.OrdinalIgnoreCase)).ToArray();
        var selected = matches.Length == 1 ? matches[0] : candidates.Length == 1 ? candidates[0] : default;
        if (selected.Window == IntPtr.Zero) return WindowMoveResult.Failed("More than one matching media window is open; switch to the intended tab once and try again.");
        if (IsIconic(selected.Window)) return WindowMoveResult.Failed("Restore the media window before moving it to another screen.");
        if (!GetWindowRect(selected.Window, out var native)) return WindowMoveResult.Failed("Windows could not read the media window position.");

        var screens = System.Windows.Forms.Screen.AllScreens.OrderBy(screen => screen.Bounds.Left).ThenBy(screen => screen.Bounds.Top).ToArray();
        if (screens.Length < 2) return WindowMoveResult.Failed("Only one active screen is available.");
        var current = System.Windows.Forms.Screen.FromHandle(selected.Window);
        var currentIndex = Array.FindIndex(screens, screen => screen.DeviceName.Equals(current.DeviceName, StringComparison.OrdinalIgnoreCase));
        if (currentIndex < 0) return WindowMoveResult.Failed("Windows could not identify the current screen.");
        var targetIndex = (currentIndex + 1) % screens.Length;
        var target = screens[targetIndex];
        var windowRect = Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom);
        var fillsScreen = NearlyEquals(windowRect, current.Bounds);
        var destination = fillsScreen ? target.Bounds : IsZoomed(selected.Window) ? target.WorkingArea : Translate(windowRect, current.WorkingArea, target.WorkingArea);
        var moved = SetWindowPos(selected.Window, IntPtr.Zero, destination.Left, destination.Top, destination.Width, destination.Height, SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpAsyncWindowPos);
        if (!moved) return WindowMoveResult.Failed("Windows refused to move the media window.");
        var label = target.Primary ? $"PRIMARY / {target.Bounds.Width}x{target.Bounds.Height}" : $"{target.DeviceName.Replace("\\\\.\\", "")} / {target.Bounds.Width}x{target.Bounds.Height}";
        return new(true, label, targetIndex + 1, screens.Length, "");
    }

    private static Rectangle Translate(Rectangle window, Rectangle current, Rectangle target)
    {
        var width = Math.Min(window.Width, target.Width);
        var height = Math.Min(window.Height, target.Height);
        var currentRangeX = Math.Max(1, current.Width - window.Width);
        var currentRangeY = Math.Max(1, current.Height - window.Height);
        var ratioX = Math.Clamp((double)(window.Left - current.Left) / currentRangeX, 0, 1);
        var ratioY = Math.Clamp((double)(window.Top - current.Top) / currentRangeY, 0, 1);
        var left = target.Left + (int)Math.Round(ratioX * Math.Max(0, target.Width - width));
        var top = target.Top + (int)Math.Round(ratioY * Math.Max(0, target.Height - height));
        return new(left, top, width, height);
    }

    private static bool NearlyEquals(Rectangle left, Rectangle right) => Math.Abs(left.Left - right.Left) <= 3 && Math.Abs(left.Top - right.Top) <= 3 && Math.Abs(left.Width - right.Width) <= 6 && Math.Abs(left.Height - right.Height) <= 6;
    private static string? ProcessName(string source)
    {
        if (source.Contains("Brave", StringComparison.OrdinalIgnoreCase)) return "brave";
        if (source.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "chrome";
        if (source.Contains("Edge", StringComparison.OrdinalIgnoreCase)) return "msedge";
        if (source.Contains("VLC", StringComparison.OrdinalIgnoreCase)) return "vlc";
        if (source.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        return null;
    }
}

static class ForegroundApp
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    public static string ProcessName() { try { GetWindowThreadProcessId(GetForegroundWindow(), out var id); return Process.GetProcessById((int)id).ProcessName; } catch { return ""; } }
}

sealed record GameFocusResult(bool Success, bool Refocused, string Error)
{
    public static GameFocusResult Focused(bool refocused) => new(true, refocused, "");
    public static GameFocusResult Failed(string error) => new(false, false, error);
}

sealed class GameWindowTracker
{
    private const int SwRestore = 9;
    private const uint GaRoot = 2;
    private readonly object gate = new();
    private Candidate? lastGame;
    private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost", "brave", "chrome", "Code", "Codex", "conhost", "devenv", "Discord",
        "dwm", "EpicGamesLauncher", "explorer", "firefox", "MediaDeck.Companion", "msedge", "Music.UI",
        "NVIDIA Overlay", "notepad", "obs64", "opera", "powershell", "pwsh", "SearchHost", "ShellExperienceHost",
        "Spotify", "StartMenuExperienceHost", "steam", "steamwebhelper", "SystemSettings", "Taskmgr", "TextInputHost",
        "vlc", "WindowsTerminal", "wmplayer"
    };

    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool value);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    private sealed record Candidate(IntPtr Window, int ProcessId, string ProcessName);

    public async Task Run(CancellationToken stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                ObserveForeground();
                await Task.Delay(500, stopping);
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task<GameFocusResult> FocusForReplayAsync()
    {
        var foreground = GetForegroundWindow();
        var current = ReadCandidate(foreground, requireGameSized: false);
        if (current is not null)
        {
            lock (gate) lastGame = current;
            return GameFocusResult.Focused(false);
        }

        Candidate? target;
        lock (gate) target = lastGame;
        if (target is null || !StillOwnedBySameProcess(target))
            return GameFocusResult.Failed("MASHR could not identify the game window. Focus the game once, then swipe again.");

        TryActivate(target.Window);
        await Task.Delay(180);
        if (GetForegroundWindow() != target.Window)
        {
            TryActivate(target.Window);
            await Task.Delay(180);
        }
        return GetForegroundWindow() == target.Window
            ? GameFocusResult.Focused(true)
            : GameFocusResult.Failed("Windows would not return focus to the game, so MASHR did not send the replay shortcut. Click the game once and retry.");
    }

    private void ObserveForeground()
    {
        var candidate = ReadCandidate(GetForegroundWindow(), requireGameSized: true);
        if (candidate is not null) lock (gate) lastGame = candidate;
    }

    private static Candidate? ReadCandidate(IntPtr window, bool requireGameSized)
    {
        try
        {
            if (window == IntPtr.Zero || !IsWindow(window) || !IsWindowVisible(window) || GetAncestor(window, GaRoot) != window) return null;
            GetWindowThreadProcessId(window, out var processId);
            if (processId == 0) return null;
            using var process = Process.GetProcessById((int)processId);
            var processName = process.ProcessName;
            if (ExcludedProcesses.Contains(processName)) return null;
            var title = new StringBuilder(260);
            if (GetWindowText(window, title, title.Capacity) == 0 || string.IsNullOrWhiteSpace(title.ToString())) return null;
            if (requireGameSized && !CoversMostOfScreen(window)) return null;
            return new Candidate(window, (int)processId, processName);
        }
        catch { return null; }
    }

    private static bool CoversMostOfScreen(IntPtr window)
    {
        if (!GetWindowRect(window, out var native)) return false;
        var bounds = Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom);
        var screen = System.Windows.Forms.Screen.FromHandle(window).Bounds;
        var visible = Rectangle.Intersect(bounds, screen);
        if (visible.Width < 640 || visible.Height < 360 || screen.Width <= 0 || screen.Height <= 0) return false;
        return (long)visible.Width * visible.Height >= (long)screen.Width * screen.Height * 65 / 100;
    }

    private static bool StillOwnedBySameProcess(Candidate candidate)
    {
        if (!IsWindow(candidate.Window)) return false;
        GetWindowThreadProcessId(candidate.Window, out var processId);
        return processId == candidate.ProcessId;
    }

    private static void TryActivate(IntPtr target)
    {
        var currentThread = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var targetThread = GetWindowThreadProcessId(target, out _);
        var attachedForeground = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
        var attachedTarget = targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread && AttachThreadInput(currentThread, targetThread, true);
        try
        {
            if (IsIconic(target)) ShowWindowAsync(target, SwRestore);
            BringWindowToTop(target);
            SetForegroundWindow(target);
        }
        finally
        {
            if (attachedTarget) AttachThreadInput(currentThread, targetThread, false);
            if (attachedForeground) AttachThreadInput(currentThread, foregroundThread, false);
        }
    }
}

static class LanDiscovery
{
    public static async Task Run(int discoveryPort, int httpPort, LanAccessPolicy lanAccess, CancellationToken stopping)
    {
        var beacon = Announce(discoveryPort, httpPort, lanAccess, stopping);
        try
        {
            using var udp = new UdpClient(discoveryPort);
            while (!stopping.IsCancellationRequested)
            {
                var request = await udp.ReceiveAsync(stopping);
                if (!lanAccess.Allows(request.RemoteEndPoint.Address)) continue;
                var message = Encoding.UTF8.GetString(request.Buffer);
                if (message == "MEDIADECK_DISCOVER")
                {
                    var reply = Encoding.UTF8.GetBytes($"MEDIADECK:{httpPort}");
                    await udp.SendAsync(reply, request.RemoteEndPoint, stopping);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        try { await beacon; } catch (OperationCanceledException) { } catch { }
    }

    private static async Task Announce(int discoveryPort, int httpPort, LanAccessPolicy lanAccess, CancellationToken stopping)
    {
        using var udp = new UdpClient { EnableBroadcast = true };
        var message = Encoding.UTF8.GetBytes($"MEDIADECK:{httpPort}");
        var addresses = new[] { lanAccess.BroadcastAddress, IPAddress.Broadcast }.Where(address => address is not null).Distinct().Cast<IPAddress>().ToArray();
        while (!stopping.IsCancellationRequested)
        {
            foreach (var address in addresses)
            {
                try { await udp.SendAsync(message, new IPEndPoint(address, discoveryPort), stopping); }
                catch (SocketException) { }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stopping);
        }
    }
}

sealed record VlcInfo(string Title, bool Playing);

static class VlcProvider
{
    private const uint WM_APPCOMMAND = 0x0319, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    private const int VK_LEFT = 0x25, VK_RIGHT = 0x27;
    private static bool playing = true;
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr target, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    private static Process? ProcessInstance() => Process.GetProcessesByName("vlc").FirstOrDefault(process => process.MainWindowHandle != IntPtr.Zero);
    public static bool Exists() => ProcessInstance() is not null;
    public static bool TryInfo(out VlcInfo info) { var process = ProcessInstance(); if (process is null) { info = new("VLC media player", false); return false; } var title = process.MainWindowTitle; const string suffix = " - VLC media player"; if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) title = title[..^suffix.Length]; info = new(string.IsNullOrWhiteSpace(title) ? "VLC media" : title, playing); return true; }
    public static bool Skip(int seconds) { var process = ProcessInstance(); if (process is null || seconds == 0) return false; var window = process.MainWindowHandle; var key = seconds < 0 ? VK_LEFT : VK_RIGHT; var presses = Math.Clamp((int)Math.Round(Math.Abs(seconds) / 10d, MidpointRounding.AwayFromZero), 1, 12); for (var index = 0; index < presses; index++) { PostMessage(window, WM_KEYDOWN, (IntPtr)key, IntPtr.Zero); PostMessage(window, WM_KEYUP, (IntPtr)key, IntPtr.Zero); } return true; }
    public static bool Control(string command) { var process = ProcessInstance(); if (process is null) return false; var window = process.MainWindowHandle; var appCommand = command switch { "play" or "pause" => 14, "stop" => 13, "next" => 11, "previous" => 12, _ => 0 }; if (appCommand != 0) { SendMessage(window, WM_APPCOMMAND, window, (IntPtr)(appCommand << 16)); if (command is "play" or "pause") playing = !playing; if (command == "stop") playing = false; return true; } if (command is "back10" or "forward10") return Skip(command == "back10" ? -10 : 10); return false; }
    public static byte[]? Capture() { var process = ProcessInstance(); if (process is null || !GetWindowRect(process.MainWindowHandle, out var rect)) return null; var width = Math.Clamp(rect.Right - rect.Left, 320, 1920); var height = Math.Clamp(rect.Bottom - rect.Top, 180, 1080); try { using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb); using (var graphics = Graphics.FromImage(bitmap)) { var dc = graphics.GetHdc(); try { if (!PrintWindow(process.MainWindowHandle, dc, 2)) return null; } finally { graphics.ReleaseHdc(dc); } } using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Jpeg); return stream.ToArray(); } catch { return null; } }
}

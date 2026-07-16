using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

const int HttpPort = 43821;
const int DiscoveryPort = 43822;

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
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

var app = builder.Build();
var security = new SecurityState();
var youtube = new YouTubeBridge();
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

    if (path == "/" || path == "/api/health" || path == "/api/pair")
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

app.MapGet("/api/health", () => Results.Json(new { service = "MediaDeck", paired = security.IsPaired, lanMode = lanAccess.Mode, bindAddress = lanAccess.IsEnabled ? lanAccess.LocalAddress?.ToString() : "loopback" }));
app.MapPost("/api/pair", (HttpContext context) =>
{
    var code = context.Request.Headers["X-MediaDeck-Pairing-Code"].ToString();
    if (security.TryPair(code, out var key))
        return Results.Json(new { key, algorithm = "HMAC-SHA256", clockWindowSeconds = 30 });
    return security.IsPairingOpen
        ? Results.Json(new { error = "Pairing code is incorrect." }, statusCode: StatusCodes.Status401Unauthorized)
        : Results.Json(new { error = "Pairing is closed. Use the tray icon to reset phone pairing." }, statusCode: StatusCodes.Status409Conflict);
}).RequireRateLimiting("pair");

app.MapGet("/api/now", async () =>
{
    var session = await Session();
    if (preferVlc && VlcProvider.TryInfo(out var vlc))
        return Results.Json(new { title = vlc.Title, artist = "VLC media player", source = "VLC.PC", playing = vlc.Playing, positionMs = 0L, durationMs = 0L, shuffle = false, repeat = "none", youtubeAvailable = youtube.IsFresh });
    if (session is null)
        return Results.Json(new { title = "Nothing playing", artist = "Start YouTube Music or another player on this PC", source = "Windows", playing = false, positionMs = 0L, durationMs = 0L, shuffle = false, repeat = "none", youtubeAvailable = youtube.IsFresh });
    var media = await session.TryGetMediaPropertiesAsync();
    var playback = session.GetPlaybackInfo();
    var timeline = session.GetTimelineProperties();
    var duration = Math.Max(0, (timeline.EndTime - timeline.StartTime).Ticks / TimeSpan.TicksPerMillisecond);
    var position = TimelineClock.PositionMs(playback, timeline);
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
        youtubeAvailable = youtube.IsFresh
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

app.MapPost("/api/control/{command}", async (string command) =>
{
    if (command == "alttab") { MediaKeys.AltTab(); return Results.Ok(); }
    if (command == "instantreplay") { MediaKeys.InstantReplay(); return Results.Ok(); }
    if (command == "altdown") { MediaKeys.BeginAltTab(); return Results.Ok(); }
    if (command == "altup") { MediaKeys.EndAltTab(); return Results.Ok(); }
    if (command is "arrowleft" or "arrowright") { MediaKeys.SwitcherArrow(command == "arrowleft" ? -1 : 1); return Results.Ok(); }
    if (command is "mute" or "volumeup" or "volumedown")
    {
        MediaKeys.Tap(command switch { "mute" => 0xAD, "volumedown" => 0xAE, _ => 0xAF });
        return Results.Ok();
    }
    var session = await Session();
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

app.MapGet("/", () => "MediaDeck Companion");
if (lanAccess.IsEnabled) _ = LanDiscovery.Run(DiscoveryPort, HttpPort, lanAccess, app.Lifetime.ApplicationStopping);
TrayApplication.Start(security, app.Lifetime, lanAccess);
app.Run();

sealed class SecurityState
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, long> usedNonces = new();
    private readonly string directory;
    private readonly string keyPath;
    private readonly string pairedPath;
    private readonly string codePath;
    private byte[] key;
    private string pairingCode = "";
    private bool pairingOpen;

    public SecurityState()
    {
        directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaDeck");
        keyPath = Path.Combine(directory, "device.key");
        pairedPath = Path.Combine(directory, "paired.marker");
        codePath = Path.Combine(directory, "pairing-code.txt");
        Directory.CreateDirectory(directory);
        key = LoadOrCreateKey();
        pairingOpen = !File.Exists(pairedPath);
        if (pairingOpen) OpenPairingCode(); else DeleteCodeFile();
    }

    public bool IsPaired { get { lock (gate) return !pairingOpen; } }
    public bool IsPairingOpen { get { lock (gate) return pairingOpen; } }
    public string PairingCode { get { lock (gate) return pairingOpen ? pairingCode : "already paired"; } }

    public bool TryPair(string candidate, out string encodedKey)
    {
        lock (gate)
        {
            encodedKey = "";
            if (!pairingOpen || candidate.Length != 6 || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(pairingCode))) return false;
            encodedKey = Convert.ToBase64String(key);
            pairingOpen = false;
            File.WriteAllText(pairedPath, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            DeleteCodeFile();
            return true;
        }
    }

    public void ResetPairing()
    {
        lock (gate)
        {
            key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(keyPath, Convert.ToBase64String(key));
            if (File.Exists(pairedPath)) File.Delete(pairedPath);
            pairingOpen = true;
            usedNonces.Clear();
            OpenPairingCode();
        }
    }

    public bool Verify(HttpRequest request)
    {
        if (!long.TryParse(request.Headers["X-MediaDeck-Time"], NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp)) return false;
        if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp) > 30) return false;
        var nonce = request.Headers["X-MediaDeck-Nonce"].ToString();
        if (nonce.Length is < 16 or > 64 || nonce.Any(ch => !char.IsAsciiLetterOrDigit(ch))) return false;
        var supplied = request.Headers["X-MediaDeck-Signature"].ToString();
        byte[] suppliedBytes;
        try { suppliedBytes = Convert.FromBase64String(supplied); }
        catch (FormatException) { return false; }
        var canonical = $"{request.Method.ToUpperInvariant()}\n{request.Path}{request.QueryString}\n{timestamp}\n{nonce}";
        byte[] keyCopy;
        lock (gate) keyCopy = key.ToArray();
        var expected = HMACSHA256.HashData(keyCopy, Encoding.UTF8.GetBytes(canonical));
        if (!CryptographicOperations.FixedTimeEquals(expected, suppliedBytes)) return false;
        var expiry = timestamp + 35;
        if (!usedNonces.TryAdd(nonce, expiry)) return false;
        if (usedNonces.Count > 512)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var old in usedNonces.Where(pair => pair.Value < now).Select(pair => pair.Key)) usedNonces.TryRemove(old, out _);
        }
        return true;
    }

    private byte[] LoadOrCreateKey()
    {
        try
        {
            if (File.Exists(keyPath))
            {
                var stored = Convert.FromBase64String(File.ReadAllText(keyPath).Trim());
                if (stored.Length == 32) return stored;
            }
        }
        catch { }
        var created = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(keyPath, Convert.ToBase64String(created));
        return created;
    }

    private void OpenPairingCode()
    {
        pairingCode = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(CultureInfo.InvariantCulture);
        File.WriteAllText(codePath, pairingCode);
    }

    private void DeleteCodeFile()
    {
        try { if (File.Exists(codePath)) File.Delete(codePath); } catch { }
    }
}

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

static class TrayApplication
{
    public static void Start(SecurityState security, IHostApplicationLifetime lifetime, LanAccessPolicy lanAccess)
    {
        var thread = new Thread(() =>
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            System.Windows.Forms.Application.Run(new TrayContext(security, lifetime, lanAccess));
        }) { IsBackground = true, Name = "MediaDeck tray" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private sealed class TrayContext : System.Windows.Forms.ApplicationContext
    {
        private readonly SecurityState security;
        private readonly IHostApplicationLifetime lifetime;
        private readonly LanAccessPolicy lanAccess;
        private readonly System.Windows.Forms.NotifyIcon icon;

        public TrayContext(SecurityState security, IHostApplicationLifetime lifetime, LanAccessPolicy lanAccess)
        {
            this.security = security;
            this.lifetime = lifetime;
            this.lanAccess = lanAccess;
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Show pairing status", null, (_, _) => ShowPairing());
            menu.Items.Add("Copy pairing code", null, (_, _) => CopyPairingCode());
            menu.Items.Add("Reset phone pairing", null, (_, _) => ResetPairing());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Exit MediaDeck", null, (_, _) => Exit());
            icon = new System.Windows.Forms.NotifyIcon
            {
                Icon = SystemIcons.Shield,
                Text = "MediaDeck Companion",
                ContextMenuStrip = menu,
                Visible = true
            };
            icon.DoubleClick += (_, _) => ShowPairing();
            ShowPairing();
        }

        private void ShowPairing()
        {
            var message = !lanAccess.IsEnabled
                ? "LAN access is off; MediaDeck is listening on this PC only. Run Configure-LanAccess to enable a phone."
                : security.IsPairingOpen
                ? $"{lanAccess.Display}. Enter pairing code {security.PairingCode} on the phone."
                : $"{lanAccess.Display}. Signed controls are enabled.";
            icon.BalloonTipTitle = "MediaDeck Companion";
            icon.BalloonTipText = message;
            icon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
            icon.ShowBalloonTip(6000);
        }

        private void CopyPairingCode()
        {
            if (!security.IsPairingOpen) { ShowPairing(); return; }
            try { System.Windows.Forms.Clipboard.SetText(security.PairingCode); } catch { }
            ShowPairing();
        }

        private void ResetPairing()
        {
            security.ResetPairing();
            ShowPairing();
        }

        private void Exit()
        {
            icon.Visible = false;
            icon.Dispose();
            lifetime.StopApplication();
            ExitThread();
        }
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
    public static void InstantReplay() { lock (AltGate) { var pressAlt = !altHeld; if (pressAlt) keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x10, 0, 0, UIntPtr.Zero); Tap(0x79); keybd_event(0x10, 0, 2, UIntPtr.Zero); if (pressAlt) keybd_event(0x12, 0, 2, UIntPtr.Zero); } }
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

static class LanDiscovery
{
    public static async Task Run(int discoveryPort, int httpPort, LanAccessPolicy lanAccess, CancellationToken stopping)
    {
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
    public static bool Control(string command) { var process = ProcessInstance(); if (process is null) return false; var window = process.MainWindowHandle; var appCommand = command switch { "play" or "pause" => 14, "stop" => 13, "next" => 11, "previous" => 12, _ => 0 }; if (appCommand != 0) { SendMessage(window, WM_APPCOMMAND, window, (IntPtr)(appCommand << 16)); if (command is "play" or "pause") playing = !playing; if (command == "stop") playing = false; return true; } if (command is "back10" or "forward10") { var key = command == "back10" ? VK_LEFT : VK_RIGHT; PostMessage(window, WM_KEYDOWN, (IntPtr)key, IntPtr.Zero); PostMessage(window, WM_KEYUP, (IntPtr)key, IntPtr.Zero); return true; } return false; }
    public static byte[]? Capture() { var process = ProcessInstance(); if (process is null || !GetWindowRect(process.MainWindowHandle, out var rect)) return null; var width = Math.Clamp(rect.Right - rect.Left, 320, 1920); var height = Math.Clamp(rect.Bottom - rect.Top, 180, 1080); try { using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb); using (var graphics = Graphics.FromImage(bitmap)) { var dc = graphics.GetHdc(); try { if (!PrintWindow(process.MainWindowHandle, dc, 2)) return null; } finally { graphics.ReleaseHdc(dc); } } using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Jpeg); return stream.ToArray(); } catch { return null; } }
}

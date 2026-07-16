using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net;
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
var lanEnabled = NetworkProfileGuard.IsPrivateOrDomain();

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls(lanEnabled ? $"http://0.0.0.0:{HttpPort}" : $"http://127.0.0.1:{HttpPort}");
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
    if (!NetworkPolicy.IsPrivateOrLoopback(remote))
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

app.MapGet("/api/health", () => Results.Json(new { service = "MediaDeck", paired = security.IsPaired }));
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
    var position = Math.Max(0, (timeline.Position - timeline.StartTime).Ticks / TimeSpan.TicksPerMillisecond);
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
        "back10" => await session.TryChangePlaybackPositionAsync(Math.Max(timeline.MinSeekTime.Ticks, timeline.Position.Ticks - TimeSpan.FromSeconds(10).Ticks)),
        "forward10" => await session.TryChangePlaybackPositionAsync(Math.Min(timeline.MaxSeekTime.Ticks, timeline.Position.Ticks + TimeSpan.FromSeconds(10).Ticks)),
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
if (lanEnabled)
{
    _ = LanDiscovery.Run(DiscoveryPort, HttpPort, app.Lifetime.ApplicationStopping);
    _ = NetworkProfileGuard.StopIfNetworkBecomesPublic(app.Lifetime, app.Lifetime.ApplicationStopping);
}
TrayApplication.Start(security, app.Lifetime, lanEnabled);
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

    public static bool IsPrivateOrLoopback(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC;
        }
        var octets = address.GetAddressBytes();
        return octets[0] == 10
            || (octets[0] == 172 && octets[1] is >= 16 and <= 31)
            || (octets[0] == 192 && octets[1] == 168)
            || (octets[0] == 169 && octets[1] == 254);
    }
}

static class NetworkProfileGuard
{
    private static readonly Guid NetworkListManagerClass = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    public static bool IsPrivateOrDomain()
    {
        object? managerObject = null;
        try
        {
            var type = Type.GetTypeFromCLSID(NetworkListManagerClass, throwOnError: true)!;
            managerObject = Activator.CreateInstance(type);
            if (managerObject is null) return false;
            dynamic manager = managerObject;
            var found = false;
            foreach (var item in manager.GetNetworks(1))
            {
                object networkObject = item;
                try
                {
                    dynamic network = networkObject;
                    found = true;
                    var category = (int)network.GetCategory();
                    if (category == 0) return false;
                }
                finally
                {
                    if (Marshal.IsComObject(networkObject)) Marshal.FinalReleaseComObject(networkObject);
                }
            }
            return found;
        }
        catch { return false; }
        finally
        {
            if (managerObject is not null && Marshal.IsComObject(managerObject)) Marshal.FinalReleaseComObject(managerObject);
        }
    }

    public static async Task StopIfNetworkBecomesPublic(IHostApplicationLifetime lifetime, CancellationToken stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stopping);
                if (!IsPrivateOrDomain())
                {
                    lifetime.StopApplication();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
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
    public static void Start(SecurityState security, IHostApplicationLifetime lifetime, bool lanEnabled)
    {
        var thread = new Thread(() =>
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            System.Windows.Forms.Application.Run(new TrayContext(security, lifetime, lanEnabled));
        }) { IsBackground = true, Name = "MediaDeck tray" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private sealed class TrayContext : System.Windows.Forms.ApplicationContext
    {
        private readonly SecurityState security;
        private readonly IHostApplicationLifetime lifetime;
        private readonly bool lanEnabled;
        private readonly System.Windows.Forms.NotifyIcon icon;

        public TrayContext(SecurityState security, IHostApplicationLifetime lifetime, bool lanEnabled)
        {
            this.security = security;
            this.lifetime = lifetime;
            this.lanEnabled = lanEnabled;
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
            var message = !lanEnabled
                ? "LAN access is blocked because Windows marks this network Public. Change it to Private, repair the firewall rules, then restart MediaDeck."
                : security.IsPairingOpen
                ? $"Enter pairing code {security.PairingCode} on the phone."
                : "Phone paired. Signed controls are enabled.";
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

static class ForegroundApp
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    public static string ProcessName() { try { GetWindowThreadProcessId(GetForegroundWindow(), out var id); return Process.GetProcessById((int)id).ProcessName; } catch { return ""; } }
}

static class LanDiscovery
{
    public static async Task Run(int discoveryPort, int httpPort, CancellationToken stopping)
    {
        try
        {
            using var udp = new UdpClient(discoveryPort);
            while (!stopping.IsCancellationRequested)
            {
                var request = await udp.ReceiveAsync(stopping);
                if (!NetworkPolicy.IsPrivateOrLoopback(request.RemoteEndPoint.Address)) continue;
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

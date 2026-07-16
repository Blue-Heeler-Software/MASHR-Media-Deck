using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Sockets;
using System.Text;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:43821");
var app = builder.Build();
string? preferredSource = null;
bool preferVlc = false;

bool MatchesForeground(string source,string process){var value=source.ToLowerInvariant();process=process.ToLowerInvariant();return value.Contains(process)||(process=="vlc"&&(value.Contains("videolan")||value.Contains("vlc")));}
async Task<GlobalSystemMediaTransportControlsSession?> Session(){
    var manager=await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();var sessions=manager.GetSessions();var foreground=ForegroundApp.ProcessName();
    if(foreground.Equals("vlc",StringComparison.OrdinalIgnoreCase)&&VlcProvider.Exists()){preferVlc=true;preferredSource=null;return null;}
    var focused=sessions.FirstOrDefault(s=>MatchesForeground(s.SourceAppUserModelId,foreground));if(focused is not null){preferVlc=false;preferredSource=focused.SourceAppUserModelId;}
    if(preferVlc&&VlcProvider.Exists())return null;
    var preferred=sessions.FirstOrDefault(s=>s.SourceAppUserModelId==preferredSource);if(preferred is not null)return preferred;
    var playing=sessions.FirstOrDefault(s=>s.GetPlaybackInfo().PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);if(playing is not null){preferredSource=playing.SourceAppUserModelId;return playing;}
    return manager.GetCurrentSession();
}

app.MapGet("/api/now", async () => {
    var session = await Session();
    if(preferVlc&&VlcProvider.TryInfo(out var vlc))return Results.Json(new{title=vlc.Title,artist="VLC media player",source="VLC.PC",playing=vlc.Playing,positionMs=0L,durationMs=0L,shuffle=false,repeat="none"});
    if (session is null) return Results.Json(new {
        title="Nothing playing", artist="Start YouTube Music or another player on this PC", source="Windows",
        playing=false, positionMs=0L, durationMs=0L, shuffle=false, repeat="none"
    });
    var media = await session.TryGetMediaPropertiesAsync();
    var playback = session.GetPlaybackInfo();
    var timeline = session.GetTimelineProperties();
    var duration = Math.Max(0, (timeline.EndTime-timeline.StartTime).Ticks/TimeSpan.TicksPerMillisecond);
    var position = Math.Max(0, (timeline.Position-timeline.StartTime).Ticks/TimeSpan.TicksPerMillisecond);
    return Results.Json(new {
        title=media.Title,
        artist=string.IsNullOrWhiteSpace(media.Artist)?media.AlbumArtist:media.Artist,
        source=session.SourceAppUserModelId,
        playing=playback.PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
        positionMs=Math.Min(position,duration), durationMs=duration,
        shuffle=playback.IsShuffleActive ?? false,
        repeat=(playback.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None).ToString().ToLowerInvariant()
    });
});

app.MapGet("/api/art", async () => {
    var session=await Session();if(preferVlc){var capture=VlcProvider.Capture();return capture is null?Results.NotFound():Results.Bytes(capture,"image/jpeg");}if(session is null)return Results.NotFound();
    var media=await session.TryGetMediaPropertiesAsync();if(media.Thumbnail is null)return Results.NotFound();
    using var stream=await media.Thumbnail.OpenReadAsync();var bytes=new byte[stream.Size];
    using var reader=new DataReader(stream);await reader.LoadAsync((uint)stream.Size);reader.ReadBytes(bytes);
    return Results.Bytes(bytes,"image/jpeg");
});

app.MapGet("/api/sessions", async () => {
    var manager=await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();var result=new List<object>();
    foreach(var session in manager.GetSessions()){var media=await session.TryGetMediaPropertiesAsync();result.Add(new{source=session.SourceAppUserModelId,title=media.Title,artist=media.Artist,playing=session.GetPlaybackInfo().PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,selected=session.SourceAppUserModelId==preferredSource});}
    return Results.Json(result);
});
app.MapPost("/api/sessions/select", async (string source) => {var manager=await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();if(!manager.GetSessions().Any(s=>s.SourceAppUserModelId==source))return Results.NotFound();preferredSource=source;return Results.Ok();});

app.MapPost("/api/seek", async (long positionMs) => {
    var session=await Session();if(session is null)return Results.NotFound();
    var timeline=session.GetTimelineProperties();
    var target=timeline.StartTime.Ticks+positionMs*TimeSpan.TicksPerMillisecond;
    target=Math.Clamp(target,timeline.MinSeekTime.Ticks,timeline.MaxSeekTime.Ticks);
    return await session.TryChangePlaybackPositionAsync(target)?Results.Ok():Results.BadRequest();
});

app.MapPost("/api/control/{command}", async (string command) => {
    if(command=="alttab"){MediaKeys.AltTab();return Results.Ok();}
    if(command=="instantreplay"){MediaKeys.InstantReplay();return Results.Ok();}
    if(command=="altdown"){MediaKeys.BeginAltTab();return Results.Ok();}
    if(command=="altup"){MediaKeys.EndAltTab();return Results.Ok();}
    if(command is "arrowleft" or "arrowright"){MediaKeys.SwitcherArrow(command=="arrowleft"?-1:1);return Results.Ok();}
    if(command is "mute" or "volumeup" or "volumedown"){
        MediaKeys.Tap(command switch{"mute"=>0xAD,"volumedown"=>0xAE,_=>0xAF});return Results.Ok();
    }
    var session=await Session();if(preferVlc)return VlcProvider.Control(command)?Results.Ok():Results.BadRequest();if(session is null)return Results.NotFound();
    var playback=session.GetPlaybackInfo();var timeline=session.GetTimelineProperties();
    bool ok=command switch{
        "play"=>await session.TryPlayAsync(),
        "pause"=>await session.TryPauseAsync(),
        "stop"=>await session.TryStopAsync(),
        "next"=>await session.TrySkipNextAsync(),
        "previous"=>await session.TrySkipPreviousAsync(),
        "back10"=>await session.TryChangePlaybackPositionAsync(Math.Max(timeline.MinSeekTime.Ticks,timeline.Position.Ticks-TimeSpan.FromSeconds(10).Ticks)),
        "forward10"=>await session.TryChangePlaybackPositionAsync(Math.Min(timeline.MaxSeekTime.Ticks,timeline.Position.Ticks+TimeSpan.FromSeconds(10).Ticks)),
        "shuffle"=>await session.TryChangeShuffleActiveAsync(!(playback.IsShuffleActive??false)),
        "repeat"=>await session.TryChangeAutoRepeatModeAsync((playback.AutoRepeatMode??MediaPlaybackAutoRepeatMode.None) switch{MediaPlaybackAutoRepeatMode.None=>MediaPlaybackAutoRepeatMode.Track,MediaPlaybackAutoRepeatMode.Track=>MediaPlaybackAutoRepeatMode.List,_=>MediaPlaybackAutoRepeatMode.None}),
        _=>false
    };
    return ok?Results.Ok():Results.BadRequest();
});

app.MapGet("/",()=>"MediaDeck Companion is running");
Console.WriteLine("MediaDeck Companion: http://0.0.0.0:43821");
_ = LanDiscovery.Run(app.Lifetime.ApplicationStopping);
app.Run();

static class MediaKeys {
    [DllImport("user32.dll")] private static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
    private static readonly object AltGate=new();private static bool altHeld;private static CancellationTokenSource? altTimeout;
    public static void Tap(int key){keybd_event((byte)key,0,0,UIntPtr.Zero);keybd_event((byte)key,0,2,UIntPtr.Zero);}
    public static void AltTab(){keybd_event(0x12,0,0,UIntPtr.Zero);keybd_event(0x09,0,0,UIntPtr.Zero);keybd_event(0x09,0,2,UIntPtr.Zero);keybd_event(0x12,0,2,UIntPtr.Zero);}
    public static void InstantReplay(){lock(AltGate){var pressAlt=!altHeld;if(pressAlt)keybd_event(0x12,0,0,UIntPtr.Zero);keybd_event(0x10,0,0,UIntPtr.Zero);Tap(0x79);keybd_event(0x10,0,2,UIntPtr.Zero);if(pressAlt)keybd_event(0x12,0,2,UIntPtr.Zero);}}
    public static void BeginAltTab(){lock(AltGate){if(!altHeld){keybd_event(0x12,0,0,UIntPtr.Zero);altHeld=true;}Tap(0x09);ArmAltTimeout();}}
    public static void SwitcherArrow(int direction){lock(AltGate){if(!altHeld)return;Tap(direction<0?0x25:0x27);ArmAltTimeout();}}
    public static void EndAltTab(){lock(AltGate){ReleaseAlt();}}
    private static void ArmAltTimeout(){altTimeout?.Cancel();var token=(altTimeout=new CancellationTokenSource()).Token;_ = Task.Delay(TimeSpan.FromSeconds(10),token).ContinueWith(_=>{if(!token.IsCancellationRequested)lock(AltGate)ReleaseAlt();},CancellationToken.None,TaskContinuationOptions.None,TaskScheduler.Default);}
    private static void ReleaseAlt(){altTimeout?.Cancel();altTimeout=null;if(altHeld){keybd_event(0x12,0,2,UIntPtr.Zero);altHeld=false;}}
}

static class ForegroundApp {
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
    public static string ProcessName(){try{GetWindowThreadProcessId(GetForegroundWindow(),out var id);return System.Diagnostics.Process.GetProcessById((int)id).ProcessName;}catch{return "";}}
}

static class LanDiscovery {
    public static async Task Run(CancellationToken stopping){
        try{using var udp=new UdpClient(43822);while(!stopping.IsCancellationRequested){var request=await udp.ReceiveAsync(stopping);var message=Encoding.UTF8.GetString(request.Buffer);if(message=="MEDIADECK_DISCOVER"){var reply=Encoding.UTF8.GetBytes("MEDIADECK:43821");await udp.SendAsync(reply,request.RemoteEndPoint,stopping);}}}
        catch(OperationCanceledException){}catch(Exception error){Console.Error.WriteLine($"LAN discovery unavailable: {error.Message}");}
    }
}


sealed record VlcInfo(string Title,bool Playing);
static class VlcProvider {
    private const uint WM_APPCOMMAND=0x0319,WM_KEYDOWN=0x0100,WM_KEYUP=0x0101;private const int VK_LEFT=0x25,VK_RIGHT=0x27;
    private static bool playing=true;
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out Rect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window,IntPtr target,uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct Rect{public int Left,Top,Right,Bottom;}
    private static System.Diagnostics.Process? Process()=>System.Diagnostics.Process.GetProcessesByName("vlc").FirstOrDefault(p=>p.MainWindowHandle!=IntPtr.Zero);
    public static bool Exists()=>Process() is not null;
    public static bool TryInfo(out VlcInfo info){var process=Process();if(process is null){info=new("VLC media player",false);return false;}var title=process.MainWindowTitle;const string suffix=" - VLC media player";if(title.EndsWith(suffix,StringComparison.OrdinalIgnoreCase))title=title[..^suffix.Length];info=new(string.IsNullOrWhiteSpace(title)?"VLC media":title,playing);return true;}
    public static bool Control(string command){var process=Process();if(process is null)return false;var window=process.MainWindowHandle;int appCommand=command switch{"play" or "pause"=>14,"stop"=>13,"next"=>11,"previous"=>12,_=>0};if(appCommand!=0){SendMessage(window,WM_APPCOMMAND,window,(IntPtr)(appCommand<<16));if(command is "play" or "pause")playing=!playing;if(command=="stop")playing=false;return true;}if(command is "back10" or "forward10"){var key=command=="back10"?VK_LEFT:VK_RIGHT;PostMessage(window,WM_KEYDOWN,(IntPtr)key,IntPtr.Zero);PostMessage(window,WM_KEYUP,(IntPtr)key,IntPtr.Zero);return true;}return false;}
    public static byte[]? Capture(){var process=Process();if(process is null||!GetWindowRect(process.MainWindowHandle,out var rect))return null;var width=Math.Clamp(rect.Right-rect.Left,320,1920);var height=Math.Clamp(rect.Bottom-rect.Top,180,1080);try{using var bitmap=new Bitmap(width,height,PixelFormat.Format24bppRgb);using(var graphics=Graphics.FromImage(bitmap)){var dc=graphics.GetHdc();try{if(!PrintWindow(process.MainWindowHandle,dc,2))return null;}finally{graphics.ReleaseHdc(dc);}}using var stream=new MemoryStream();bitmap.Save(stream,ImageFormat.Jpeg);return stream.ToArray();}catch{return null;}}
}

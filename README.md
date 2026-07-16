# MediaDeck

MediaDeck is a second-screen Android remote for PC gamers. It keeps the game focused while the phone controls the active Windows media session, shows its artwork and metadata, switches Alt+Tab windows, and triggers NVIDIA Instant Replay.

The remote has large transport, seek, volume, shuffle/repeat, Alt+Tab, and guarded swipe-to-replay controls. When a normal YouTube watch page is active, swiping up on the artwork opens a tappable 3x3 grid of the recommendations already shown in that YouTube tab.

## Security model

The phone needs no media permissions, Google login, or YouTube account access. It pairs once to the PC companion using the six-digit code shown by the MediaDeck tray icon.

- Every media/control request is authenticated with HMAC-SHA256.
- Each request has a 30-second clock window and one-use nonce, so captured requests cannot simply be replayed.
- Pairing is one-time and limited to five attempts per ten minutes. Resetting phone pairing rotates the 256-bit device key.
- The server rejects traffic that is not from loopback or a private/link-local network address.
- The YouTube browser bridge is loopback-only and cannot be reached from another LAN device.
- Request size, header time, connection lifetime, and request rate are bounded.
- The companion exposes fixed media actions only; there is no shell, command text, file upload, or arbitrary URL endpoint.

Do not forward TCP `43821` or UDP `43822` on the router, and allow the companion only on Windows **Private** firewall profiles. The transport is authenticated but is not TLS-encrypted; see [SECURITY.md](SECURITY.md) for the remaining LAN-sniffing caveat.

## Build and run

Build both parts:

```powershell
dotnet build companion/MediaDeck.Companion.csproj -c Debug
./gradlew.bat :app:assembleDebug
```

Before enabling LAN access, set the Windows network profile to **Private**, then run `companion/Install-PrivateFirewall.ps1` once from an Administrator PowerShell. The script disables stale MediaDeck rules and creates only the two narrow Private/LocalSubnet rules described above. If Windows reports the network as Public, MediaDeck binds to loopback only and will not accept phone connections.

Start `companion/bin/Debug/net10.0-windows10.0.19041.0/MediaDeck.Companion.exe`. It runs as a shield icon in the notification area rather than leaving a CLI window open.

On first connection:

1. Right-click or double-click the MediaDeck tray shield to show its one-time code.
2. On the phone, tap **PC SETTINGS**, enter the code, and tap **PAIR**. The PC address may be left blank for LAN discovery.
3. After pairing, the code closes. Use **Reset phone pairing** from the tray icon only when replacing/reinstalling the phone app.

The Android debug APK is written to `app/build/outputs/apk/debug/app-debug.apk`.

## YouTube recommendations

Windows media sessions expose playback metadata but not YouTube's recommendation list. The included browser helper reads the first nine cards that YouTube already rendered in the signed-in PC tab.

For Brave:

1. Open `brave://extensions` and enable **Developer mode**.
2. Choose **Load unpacked** and select the repository's `browser-extension` folder.
3. Reload the YouTube watch tab once.

For Chrome, use `chrome://extensions`. The helper requests access only to YouTube watch pages and `127.0.0.1:43821`; it does not request history, cookies, or general site access.

With a YouTube video open, the phone displays **SWIPE UP FOR PICKS**. Swipe upward on the artwork, then tap one of the nine thumbnails to navigate the existing YouTube tab to that video.

## Restart reliability

The companion listens on authenticated HTTP port `43821` and answers LAN discovery broadcasts on UDP port `43822`. Registering it as an **At log on** task restores reconnect-after-restart behavior without a visible console:

```powershell
$exe = (Resolve-Path './companion/bin/Debug/net10.0-windows10.0.19041.0/MediaDeck.Companion.exe').Path
$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory (Split-Path $exe)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName 'MediaDeck Companion' -Action $action -Trigger $trigger -Settings $settings -Description 'Authenticated MediaDeck LAN media companion' -Force
Start-ScheduledTask -TaskName 'MediaDeck Companion'
```

## Supported players

- YouTube and YouTube Music in Brave/Chrome
- Spotify
- VLC (focused-window fallback)
- Most players that publish a Windows global media session

MediaDeck has no analytics, account system, or cloud backend.

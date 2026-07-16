# MediaDeck

MediaDeck is a second-screen Android remote for PC gamers. Keep the game focused while the phone controls the active Windows media session, imports its artwork and metadata, and provides play/pause, previous, and next controls without Alt-Tab.

The remote also includes a draggable playback timeline, 10-second seek controls, stop, shuffle/repeat modes, and PC-wide mute/volume buttons. Availability of timeline, shuffle, and repeat depends on what the active Windows media session exposes.

## Supported players

- YouTube Music
- Spotify
- Podcast and audiobook apps
- Most apps that publish a standard Android `MediaSession`

The phone needs no media permissions or account login. The Windows companion uses the supported Windows media-session interface rather than private YouTube APIs.

## Run

1. Build the PC companion, then allow private-network firewall access if Windows asks:

   ```powershell
   dotnet build companion/MediaDeck.Companion.csproj -c Release
   ./companion/bin/Release/net10.0-windows10.0.19041.0/MediaDeck.Companion.exe
   ```

   Register the executable as an **At log on** task so it starts before the phone reconnects:

   ```powershell
   $exe = (Resolve-Path './companion/bin/Release/net10.0-windows10.0.19041.0/MediaDeck.Companion.exe').Path
   $action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory (Split-Path $exe)
   $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
   Register-ScheduledTask -TaskName 'MediaDeck Companion' -Action $action -Trigger $trigger -Description 'MediaDeck LAN media companion' -Force
   Start-ScheduledTask -TaskName 'MediaDeck Companion'
   ```
2. Install the Android app and keep the phone on the same Wi-Fi as the PC.
3. Enter the PC's LAN address (shown by `ipconfig`) and tap **Connect**.
4. Start YouTube Music, Spotify, or another media player on the PC; the game can remain focused.

After a PC restart, the companion starts automatically. If the PC receives a new address from the router, the Android app discovers it over the local network and updates its saved address automatically.

## Restart reliability

The companion listens on HTTP port `43821` and answers discovery broadcasts on UDP port `43822`. The Android app first tries its saved PC address; on failure it broadcasts `MEDIADECK_DISCOVER`, saves the responding address, and retries automatically.

If reconnection fails, verify that the **MediaDeck Companion** scheduled task is running and that Windows Firewall allows the companion on private networks.

## Architecture

- `companion/` exposes the current Windows media session over the local network.
- `MainActivity` renders PC metadata/artwork and sends transport commands.

## Privacy

MediaDeck has no analytics, account system, or cloud backend. The companion is reachable on the local network without authentication, so use it only on a trusted home/private network.

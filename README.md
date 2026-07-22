# MASHR Media Deck

![MASHR Media Deck — stay in the game](docs/images/mashr-media-deck-social-v148.png)

> Your PC keeps the game. Your phone keeps the controls.

[![Android 8+](https://img.shields.io/badge/Android-8%2B-7DD3FC?style=flat-square&logo=android&logoColor=white)](#quick-start)
[![Windows 10/11](https://img.shields.io/badge/Windows-10%20%2F%2011-A78BFA?style=flat-square&logo=windows11&logoColor=white)](#quick-start)
[![Linux scaffold](https://img.shields.io/badge/Linux-provider%20scaffold-8F8EA3?style=flat-square&logo=linux&logoColor=white)](clients/linux/README.md)
[![macOS scaffold](https://img.shields.io/badge/macOS-provider%20scaffold-8F8EA3?style=flat-square&logo=apple&logoColor=white)](clients/macos/README.md)
[![Local only](https://img.shields.io/badge/network-local%20only-22C55E?style=flat-square)](#security-model)
[![No cloud account](https://img.shields.io/badge/cloud-none-171923?style=flat-square)](#security-model)
[![No browser extension](https://img.shields.io/badge/browser%20extension-not%20required-22C55E?style=flat-square)](#no-extension-needed)
[![License: MIT](https://img.shields.io/badge/license-MIT-F7C948?style=flat-square)](LICENSE)

MASHR Media Deck is a second-screen Android remote built for PC gamers who do not want to Alt+Tab out of a game just to manage music or video. The Pixel shows the selected Windows media session, artwork, live timeline, chapter markers, and large controls while the game keeps focus.

![MASHR Media Deck 1.9.11 showing split annotation seeking and the rebalanced control rows on a Pixel 7](docs/images/pixel7-annotation-seek-v1911.png)

## No extension needed

**Everything in the core deck works with the Android app and Windows companion alone.** That includes YouTube artwork and metadata, live progress, chapter markers, the tappable **SCENES** list, Like/Dislike/Subscribe, actual YouTube player volume, transport and system-volume controls, monitor switching, held Alt+Tab, NVIDIA replay, local pairing, and automatic reconnect.

The browser helper is not required for any control shown above. It exists for one separate, optional extra: the 3×3 related-video grid. Ignore or delete `browser-extension` and the main experience is unchanged.

## What it does

- Large play/pause, previous/next, volume, mute, shuffle, repeat, stop, and configurable Back/Ahead controls.
- Split Back/Ahead controls: tap the blue **SCENE** edge to jump between annotations, or use the dark body for the default skip set in **PC SETTINGS**.
- Live artwork, title, artist, elapsed time, remaining time, and continuously updating progress.
- Tappable YouTube creator chapters as progress markers and a **SCENES** list—no extension required.
- Extension-free YouTube **LIKE**, **DISLIKE**, and **SUB** controls through the selected browser window's Windows accessibility surface.
- A compact **YT VOL** slider that reads and changes the selected YouTube player's own 0–100 volume without changing Windows master volume.
- Guarded NVIDIA Instant Replay slider that shows whether the buffer is off, arms it explicitly, and labels the armed action **RECORD LAST 2:00 OF GAME** (using the detected buffer length).
- Hold-to-use Alt+Tab: keep the yellow control held and use **PREV/NEXT** as window-switcher arrows.
- Move the selected media window to the next monitor without stealing focus.
- Save a PC screenshot using NVIDIA Overlay's configured shortcut, with a Windows fallback.
- Automatic reconnect through a background Windows tray companion.
- Optional 3×3 related-video grid from a narrowly scoped helper—the only feature that uses it.

See the [screenshot gallery](docs/SCREENSHOTS.md) and [press kit](docs/PRESS-KIT.md).

## See it in action

Every screen here is a real Pixel 7 capture, not a drawn app mock-up. The lead scene-navigation capture is from 1.9.11; supporting interaction captures are retained from 1.4.8.

[Watch the rebuilt 30-second VLC demo clip](docs/media/mashr-media-deck-demo-v148.mp4), whose artwork is composed from the same real Pixel capture.

<table>
  <tr>
    <td colspan="2" valign="top">
      <img src="docs/images/pixel7-youtube-actions-v148.png" alt="MASHR Media Deck with dominant Like, smaller Dislike, Subscribe, compact held Alt Tab, and YouTube volume controls"><br>
      <strong>React without surfacing the browser.</strong><br>
      The larger Like, smaller Dislike, Subscribe, compact multitouch Alt+Tab, and tiny actual-player volume slider work through the authenticated companion without an extension.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="docs/images/pixel7-annotation-seek-v1911.png" alt="Split blue scene jumps and rebalanced media controls on MASHR Media Deck 1.9.11"><br>
      <strong>Annotations are one blue tap away.</strong><br>
      Blue Scene edges jump to adjacent creator annotations; the dark Back/Ahead bodies use the default skip from PC Settings.
    </td>
    <td width="50%" valign="top">
      <img src="docs/images/pixel7-scene-list-v148.png" alt="Tappable YouTube scene list on MASHR Media Deck"><br>
      <strong>Tap straight to the good bit.</strong><br>
      The chapter list highlights the current scene and jumps to any creator timestamp.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="docs/images/pixel7-alt-tab-held-v148.png" alt="Alt held mode with previous and next window controls"><br>
      <strong>Alt+Tab without leaving the phone.</strong><br>
      Hold the red state and use the renamed window arrows with a second finger.
    </td>
    <td width="50%" valign="top">
      <img src="docs/images/pixel7-replay-gesture-v148.png" alt="Guarded NVIDIA Instant Replay swipe in progress"><br>
      <strong>Hard to trigger by accident.</strong><br>
      Replay requires a deliberate left-to-right swipe and reports progress before it acts.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="docs/images/pixel7-screenshot-button-v148.png" alt="MASHR Media Deck Screenshot and Move Screen controls"><br>
      <strong>Capture without leaving the game.</strong><br>
      The dedicated button uses NVIDIA's configured Screenshot shortcut, with a Windows fallback—and the same deck works with VLC.
    </td>
    <td width="50%" valign="top">
      <img src="docs/images/pixel7-local-pairing-v148.png" alt="Local one-time pairing screen with no YouTube login"><br>
      <strong>Pair locally, not with a media account.</strong><br>
      Open the app and it appears in the PC dashboard. One click pairs that exact phone and IP; the local code remains only as a fallback.
    </td>
  </tr>
  <tr>
    <td colspan="2" valign="top">
      <img src="docs/images/pixel7-replay-control-v148.png" alt="MASHR Media Deck gamer controls with Record last 2 minutes of game replay action"><br>
      <strong>The action says what the gamer gets.</strong><br>
      When NVIDIA's buffer is armed, the green control reads <strong>RECORD LAST 2:00 OF GAME</strong> instead of relying on replay jargon.
    </td>
  </tr>
</table>

## Designed for the couch-and-keyboard problem

MASHR Media Deck is not a general remote-desktop app. It exposes a small allowlisted media-control surface so a phone can handle the routine interruptions while the PC remains on the game:

| Moment | Phone action |
| --- | --- |
| A track is too loud | Tap **VOL −** or **MUTE** |
| A video drifts into filler | Tap the blue **SCENE** edge or the dark **AHEAD** control |
| A video earns a reaction | Tap **LIKE**, **DISLIKE**, or **SUB** |
| YouTube itself is too loud | Drag the tiny **YT VOL** slider |
| The media window is on the wrong display | Tap **MOVE SCREEN** |
| Something worth clipping just happened | Swipe **RECORD LAST 2:00 OF GAME** |
| A different PC window is needed | Hold **ALT + TAB**, then tap **PREV/NEXT** |

## Security model

The phone needs no Google login, YouTube account access, Android media permission, or cloud account. An unpaired phone announces a short-lived request on the local network; the PC grants it only when you click **PAIR THIS DEVICE** beside the expected device name and IP. The two-minute, six-digit flow remains as a recovery fallback.

- Every paired controller receives its own random 256-bit key, and every media/control request is authenticated with HMAC-SHA256.
- Requests have a 30-second clock window and one-use nonce.
- Nearby requests expire after 45 seconds. Approval creates a 30-second, one-use claim bound to that request's random token, ephemeral RSA public key, and source IP. The HMAC key crosses the LAN only as an RSA-OAEP-SHA256 envelope that the phone's private key can open.
- The PC dashboard shows every paired controller, its last address and recent activity, and can revoke one controller independently.
- Strict `PairedPhone` LAN mode accepts one phone IP. Multi-device `SameSubnet` mode stays bound to one PC interface and one directly connected subnet; unknown devices may ask to pair, but receive no key or control authority without an explicit dashboard click.
- Control commands are fixed and allowlisted—there is no shell, arbitrary URL, file upload, process ID, window handle, or coordinate endpoint.
- YouTube actions target only named accessibility controls in the selected YouTube browser window; the phone cannot send arbitrary clicks or keys.
- YouTube volume accepts only a 0–100 level; the companion derives the exact verified Volume slider and browser render host instead of accepting caller-provided coordinates or key codes.
- Browser-helper traffic is loopback-only.
- YouTube chapters use the public page for the exact selected watch URL and send no browser cookies.

Do not forward TCP `43821` or UDP `43822` on a router. Read [SECURITY.md](SECURITY.md) for the complete threat model and remaining HTTP confidentiality limitation.

## Quick start

### 1. Build

Requirements:

- Windows 10 or 11
- .NET 10 SDK
- JDK 17
- Android SDK 36 / Build Tools 36
- Android device with USB debugging for direct deployment

```powershell
dotnet build companion/MediaDeck.Companion.csproj -c Debug
./gradlew.bat :app:assembleDebug
```

The Android APK is written to:

```text
app/build/outputs/apk/debug/app-debug.apk
```

### 2. Restrict LAN access

LAN access is off by default. For one controller, enable the strict paired-phone mode from an Administrator Command Prompt:

```bat
companion\Configure-LanAccess.cmd PairedPhone PHONE_IP PC_IP
```

For WPS-style pairing of several controllers without editing the firewall for each phone, enable `SameSubnet` instead:

```bat
companion\Configure-LanAccess.cmd SameSubnet PHONE_IP PC_IP NETWORK_CIDR
```

Both modes keep the existing Windows private/public network profile unchanged, disable stale broad rules, and bind the companion to one chosen PC interface. `PairedPhone` scopes the firewall to one IP. `SameSubnet` lets devices on that directly connected subnet reach the bounded HTTP listener and submit expiring nearby requests, but only a local dashboard click grants one exact request; controls still require a device-specific signed key.

### 3. Pair the phone

1. Start `companion/bin/Debug/net10.0-windows10.0.19041.0/MediaDeck.Companion.exe`; its dashboard opens visibly.
2. Open MASHR Media Deck on an unpaired phone. It discovers the PC and appears under **NEARBY CONTROLLERS**.
3. Confirm the phone name and IP, then click **PAIR THIS DEVICE** once. The phone collects its unique key automatically.
4. If discovery is unavailable, click **START CODE MODE** and enter the two-minute fallback code under **PC SETTINGS** on the phone.
5. Use **REVOKE SELECTED** in the dashboard if one controller should lose access.

The stable executable, Android package, scheduled-task name, discovery token, HMAC headers, and pairing-storage path retain their original `MediaDeck` identifiers so existing installs upgrade without losing pairing or restart behavior.

## YouTube scenes and actions

**No extension is used for this.**

For Brave, Chrome, and Edge, the companion reads the address bar of the unambiguous selected media window through Windows UI Automation. When it is an exact YouTube watch URL, creator-published description timestamps become progress markers. Tap **SCENES** between elapsed and remaining time to open the full chapter list, or use the blue edge of **BACK/AHEAD** to jump directly to the adjacent annotation. When there is no annotation in that direction, the blue edge disappears and the dark control uses the default skip duration from **PC SETTINGS**. The deck deliberately leaves that duration off the button label so the scene mechanic stays visually obvious. The same extension-free accessibility surface activates only the visible, named Like, Dislike, Subscribe, or Volume control; Subscribe never doubles as an unsubscribe action. **YT VOL** changes the webpage player's own value while leaving Windows master volume and the physical cursor alone.

## Optional extra: related-video grid

The 3×3 related-video grid is the one feature that cannot be recovered from Windows media sessions or the public watch page. It is separate from playback, thumbnails, progress, controls, and scenes.

<details>
<summary>Install the narrowly scoped helper for this extra only</summary>

To enable only the related-video grid:

1. Open `brave://extensions` or `chrome://extensions`.
2. Enable **Developer mode**.
3. Choose **Load unpacked** and select `browser-extension`.
4. Reload the YouTube watch page.

The helper requests access only to YouTube watch pages and `127.0.0.1:43821`; it requests no history, cookies, or general browsing access.

</details>

## NVIDIA Instant Replay

MASHR Media Deck reads NVIDIA Overlay's local `ShareSettings.json` for:

- whether Instant Replay is enabled;
- the configured rolling-buffer duration;
- the configured `DVRSave` and `DVRToggle` shortcuts.

When replay is off, the slider is orange and says **SWIPE TO ARM GAME REPLAY**. Once NVIDIA reports the buffer enabled, it turns green and becomes **RECORD LAST 2:00 OF GAME**, with the duration taken from NVIDIA's configured rolling buffer. The remote does not expose a disarm action, so stale phone state cannot accidentally switch the buffer off.

The companion remembers the last foreground game-sized window. Before saving a replay, it restores that window if a browser, media player, or controller app has taken focus, verifies that the game is foreground again, and only then sends NVIDIA's configured save shortcut. If Windows refuses the focus change or no game has been seen yet, MASHR reports a failure and sends no replay shortcut.

## Restart reliability

Register the companion as an **At log on** task so it reconnects without a visible terminal:

```powershell
$exe = (Resolve-Path './companion/bin/Debug/net10.0-windows10.0.19041.0/MediaDeck.Companion.exe').Path
$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory (Split-Path $exe)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName 'MediaDeck Companion' -Action $action -Trigger $trigger -Settings $settings -Description 'Authenticated MASHR Media Deck LAN companion' -Force
Start-ScheduledTask -TaskName 'MediaDeck Companion'
```

## Supported players

- YouTube and YouTube Music in Brave, Chrome, or Edge
- Spotify
- VLC focused-window fallback
- Most players that publish a Windows global media session

## Linux and macOS companion scaffolds

The complete phone-pairing companion currently targets Windows. Buildable provider scaffolds now live in [`clients/linux`](clients/linux/README.md) and [`clients/macos`](clients/macos/README.md): Linux reads MPRIS through `playerctl`, while macOS reads Apple Music or Spotify through fixed AppleScript calls.

Both scaffolds emit the shared `/api/now` snapshot shape and expose a small allowlisted control CLI. They intentionally open no network ports and cannot pair with Android yet. [`clients/PROTOCOL.md`](clients/PROTOCOL.md) records the transport, HMAC, replay-protection, source-IP, and rate-limit requirements that must be implemented before either port enables LAN access.

## Project status

MASHR Media Deck `1.9.11` is an open-source, owner-tested developer preview for a Pixel 7 and Windows 11 gaming PC. Linux and macOS are contributor scaffolds, not released companions. The project has no analytics, cloud backend, advertising, or account system.

Release history is in [CHANGELOG.md](CHANGELOG.md).

## License

MASHR Media Deck is free and open-source software released under the [MIT License](LICENSE). Copyright © 2026 Blue Heeler Software.

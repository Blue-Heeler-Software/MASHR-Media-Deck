# Changelog

All notable MASHR Media Deck changes are recorded here.

## 1.9.16 — 2026-07-30

### Fixed

- Replaced the single-address `PairedPhone` gate with an explicit allowlist of up to sixteen exact private phone IPs. Each address is route-validated independently, Windows Firewall remains restricted to only those remotes, and the companion binds only to the required private PC interface addresses.
- Kept routed private-LAN support for phones on different subnets without falling back to the broader `SameSubnet` exposure.
- Replaced superseded pairing retries from the same device and source address so the PC dashboard cannot remain selected on an expired MATCH code after the phone rotates its ephemeral request.
- Reworked the desktop dashboard into a state-driven four-step flow that names the next action, distinguishes firewall-allowed IPs from paired controllers, explains MATCH verification, disables destructive reset when nothing is paired, and shows encrypted-key delivery after approval.
- Made RSA-PSS grant verification portable across Android Conscrypt, standard Java, and OEM/Bouncy Castle provider aliases without changing the signed pairing protocol.

## 1.9.15 — 2026-07-28

### Added

- Added the off-by-default **Pad Controls Pointer** shared setting. A one-finger drag beginning on unused deck space now sends coalesced, bounded relative pointer movement over the existing signed connection without generating clicks or taking over buttons, sliders, artwork, replay, microphone mute, or multitouch controls.

### Fixed

- Restored strict `PairedPhone` reconnects when a phone and PC use different routed private subnets. The companion now follows the selected Windows route while keeping both the firewall and application allowlist pinned to that phone's exact IP; broadcast discovery remains direct-subnet-only.

## 1.9.14 — 2026-07-28

### Security

- Replaced the cleartext fallback-key exchange with a single PC-approved nearby flow that shows the same transcript-derived six-digit **MATCH** code on the phone and PC.
- Added a persistent DPAPI-protected PC identity and RSA-PSS-signed pairing grants, preventing an active LAN interceptor from substituting its key when the user compares the code.
- Added nonce-bound HMAC authentication for every protected response, while continuing to reject replayed requests and now rejecting authenticated request bodies.
- Limited nearby pairing to explicit two-minute windows, reduced its request rate, capped each source at two pending requests, and stopped unsolicited discovery beacons outside the window.
- Wrapped Android controller credentials with Android Keystore, excluded them from backup and device transfer, and migrated existing phone and Windows credentials in place.
- Restricted new firewall rules to trusted Private profiles, retired wildcard rule cleanup, pinned the Gradle distribution and GitHub Actions, and added core security CI plus Dependabot configuration.
- Invalidated older `Profile Any` LAN configuration markers so upgrades start loopback-only until the owner explicitly reconfigures on a trusted Private network; existing paired-device keys remain intact.
- Prevented release APK packaging without an explicit private signing identity. Debug APKs remain available only for local ADB development.

## 1.9.13 — 2026-07-28

### Changed

- Gave the oversized **VOLUME DOWN** and **VOLUME UP** controls distinct mild cool treatments: soft slate blue for down and restrained teal for up.

## 1.9.12 — 2026-07-27

### Added

- Added a compact PC-wide microphone mute beside the guarded game-replay swipe. It reads and toggles every active Windows capture endpoint, stays black with a red mute slash while microphones are live, and inverts to red with a black glyph while muted.

## 1.9.11 — 2026-07-23

### Added

- Added an extension-free **JUMP** edge for exact YouTube watch pages. It invokes YouTube Premium's own embedded-segment/frequently-skipped marker and falls back to the configured skip when the account or video exposes no marker.
- Added a visible Windows companion dashboard with a large timed pairing code, network-gate status, paired-controller roster, online/last-seen state, and individual revocation.
- Added two-minute additive pairing mode for up to 16 Android controllers, with a distinct 256-bit HMAC key and persistent device identity for every phone.
- Added default one-click nearby pairing: unpaired phones announce an expiring request, the dashboard approves the selected name/IP once, and only that phone can collect its one-use token/IP-bound key. The numeric code remains as fallback.
- Nearby approval wraps the new HMAC key with the phone's ephemeral RSA-2048 public key using OAEP-SHA256, so the raw credential never crosses the WLAN.
- Added buildable Linux (`playerctl`/MPRIS) and macOS (Music/Spotify AppleScript) provider scaffolds, a shared companion protocol contract, and CI build jobs. The scaffolds intentionally open no network ports until pairing and signed-request parity exists.

### Documentation

- Re-captured the 1.9.11 lead deck image on the deployed Pixel 7 with the final Mute, Shuffle, Screenshot, and Like proportions.
- Released MASHR Media Deck as free and open-source software under the MIT License.
- Re-captured the complete gallery on a real Pixel 7 running 1.4.8, cache-busted every published image path, and rebuilt the VLC demo artwork from the real UI instead of a drawn app mock-up.
- Made the extension boundary explicit: the complete core deck is extension-free; only the optional related-video grid uses the helper.
- Updated README, press copy, security notes, screenshot captions, and social artwork around the current extension-free controls and cross-platform scaffold boundary.

### Changed

- Renamed the large system-volume controls from ambiguous `DOWN` / `UP` labels to explicit **VOLUME DOWN** / **VOLUME UP** labels.
- Existing single-phone installs migrate their key into the new roster without forcing the Pixel to pair again; future Android requests identify the controller and update its dashboard activity state.
- Discovery now combines directed interface broadcasts with an outbound same-port companion beacon, so first-run phones can find a tightly firewalled PC without widening the firewall's exact local-address scope.
- Documented `SameSubnet` as the explicit firewall mode for WPS-style multi-device pairing while retaining strict one-IP `PairedPhone` mode.
- Split Back/Ahead into a dark configurable skip action and a blue adjacent-annotation action that appears only when a scene target exists; removed the duration from the control label and icon.
- Added a 1–120 second default skip setting to the phone's PC Settings screen and a bounded, authenticated companion endpoint for exact signed skips.
- Changed **MOVE SCREEN** from cyan to golden brown so it is visually distinct from blue scene-navigation actions.
- Replaced the text-heavy control deck with a consistent scalable icon system while retaining short labels for instant recognition and accessibility.
- Instant Replay now restores and verifies the remembered game window before sending NVIDIA's save shortcut. If the game cannot be focused, the companion returns a real error instead of falsely reporting a saved clip.
- Renamed the blue **MOVE MEDIA** control to the shorter, clearer **MOVE SCREEN** label.
- Renamed the armed replay control to **RECORD LAST 2:00 OF GAME** so its gameplay-capture purpose is obvious at a glance.
- Reduced the outer deck gutter, card inset, and control-row spacing so the Pixel 7 layout uses more of the available screen width while retaining safe rounded edges.
- Extended the control stack toward the Pixel navigation area, moved Mute beside Shuffle/Repeat/Stop, and gave both remaining volume controls equal oversized targets.
- Added a dedicated screenshot button that uses NVIDIA Overlay's configured shortcut when available and falls back to Windows' saved-screenshot shortcut.
- Reserved a consistent two-line title area so short VLC titles no longer make the deck collapse vertically.
- Widened Screenshot beyond Mute while keeping it narrower than Like, and shortened Shuffle slightly within the four-control mode row.
- Replaced the full-width Alt+Tab bar with a compact multitouch control beside extension-free YouTube Like, Dislike, and Subscribe actions.
- Made Like larger than Dislike, compressed the YouTube action row, and added a tiny slider for the selected YouTube player's actual 0–100 volume.

## 1.4.0 — 2026-07-17

### Added

- MASHR Media Deck product identity, Android launcher mark, companion metadata, and promotional pack.
- Extension-free YouTube chapter extraction from the exact selected watch URL.
- Cyan progress markers and a tappable **SCENES** chapter picker.
- NVIDIA Instant Replay state, configured buffer length, and configured hotkey discovery.
- Separate guarded replay-arm and replay-save actions.
- Safe media-window cycling across monitors without focus activation.

### Fixed

- NVIDIA replay gesture no longer sends the toggle shortcut when the user intends to save.
- Live timeline now extrapolates from the Windows media clock instead of freezing on stale snapshots.
- Artwork retries after transient thumbnail failures.
- Pixel 7 layout fits in one fixed viewport without vertical scrolling or navigation-bar overscan.
- Companion reconnects through the background scheduled task after Windows restart.

### Security

- Paired-phone LAN restriction, HMAC signatures, nonce replay prevention, bounded requests, and loopback-only browser endpoints.
- Chapter scraping is limited to exact YouTube watch URLs, sends no cookies, and caps public-page responses.
- Monitor and replay commands accept no arbitrary process, window, key, URL, or coordinate input from the phone.

## 1.0.0 — 2026-07-15

- Initial Android remote and Windows media companion.

# Changelog

All notable MASHR Media Deck changes are recorded here.

## 1.9.11 — 2026-07-23

### Added

- Added buildable Linux (`playerctl`/MPRIS) and macOS (Music/Spotify AppleScript) provider scaffolds, a shared companion protocol contract, and CI build jobs. The scaffolds intentionally open no network ports until pairing and signed-request parity exists.

### Documentation

- Re-captured the 1.9.11 lead deck image on the deployed Pixel 7 with the final Mute, Shuffle, Screenshot, and Like proportions.
- Released MASHR Media Deck as free and open-source software under the MIT License.
- Re-captured the complete gallery on a real Pixel 7 running 1.4.8, cache-busted every published image path, and rebuilt the VLC demo artwork from the real UI instead of a drawn app mock-up.
- Made the extension boundary explicit: the complete core deck is extension-free; only the optional related-video grid uses the helper.
- Updated README, press copy, security notes, screenshot captions, and social artwork around the current extension-free controls and cross-platform scaffold boundary.

### Changed

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

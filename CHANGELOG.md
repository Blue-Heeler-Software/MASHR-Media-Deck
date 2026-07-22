# Changelog

All notable MASHR Media Deck changes are recorded here.

## Unreleased

### Added

- Added buildable Linux (`playerctl`/MPRIS) and macOS (Music/Spotify AppleScript) provider scaffolds, a shared companion protocol contract, and CI build jobs. The scaffolds intentionally open no network ports until pairing and signed-request parity exists.

### Documentation

- Released MASHR Media Deck as free and open-source software under the MIT License.
- Re-captured the complete nine-image Pixel 7 gallery on 1.4.7, including extension-free YouTube scenes, the current action row, chapter jumping, held Alt+Tab, guarded replay, local pairing, and a locally generated VLC demo.
- Made the extension boundary explicit: the complete core deck is extension-free; only the optional related-video grid uses the helper.
- Updated README, press copy, security notes, screenshot captions, and social artwork around the current extension-free controls and cross-platform scaffold boundary.

### Changed

- Renamed the armed replay control to **RECORD LAST 2:00 OF GAME** so its gameplay-capture purpose is obvious at a glance.
- Reduced the outer deck gutter, card inset, and control-row spacing so the Pixel 7 layout uses more of the available screen width while retaining safe rounded edges.
- Extended the control stack toward the Pixel navigation area, made Mute narrower, and gave both volume controls more width, height, and label emphasis.
- Added a dedicated screenshot button that uses NVIDIA Overlay's configured shortcut when available and falls back to Windows' saved-screenshot shortcut.
- Reserved a consistent two-line title area so short VLC titles no longer make the deck collapse vertically.
- Matched the Screenshot button to Mute's width and moved its label onto two clear lines.
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

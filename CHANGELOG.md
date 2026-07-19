# Changelog

All notable MASHR Media Deck changes are recorded here.

## Unreleased

### Documentation

- Added a six-screen real Pixel 7 product tour covering extension-free YouTube scenes, chapter jumping, Alt+Tab, guarded replay, local pairing, and another Windows media session.
- Made the extension boundary explicit: the complete core deck is extension-free; only the optional related-video grid uses the helper.
- Updated press copy and social artwork around the extension-free core experience.

### Changed

- Reduced the outer deck gutter, card inset, and control-row spacing so the Pixel 7 layout uses more of the available screen width while retaining safe rounded edges.
- Extended the control stack toward the Pixel navigation area, made Mute narrower, and gave both volume controls more width, height, and label emphasis.

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

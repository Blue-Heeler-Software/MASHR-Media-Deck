# MASHR Media Deck YouTube Bridge

> **You do not need this for MASHR Media Deck.**

Artwork, metadata, live progress, YouTube chapter markers, the tappable **SCENES** list, playback controls, monitor switching, Alt+Tab, NVIDIA replay, pairing, and reconnect all work without a browser extension.

This optional unpacked Chrome/Brave helper adds one extra feature only: the 3×3 related-video grid. It reads the nine recommendation cards already visible beside the current `youtube.com/watch` video, sends their video IDs and titles to MASHR Media Deck over loopback (`127.0.0.1`), then polls loopback for a video selected on the phone.

It does not request browsing history, cookies, account data, or access to non-YouTube pages. It cannot ask the companion to fetch arbitrary URLs.

## Install in Brave

1. Open `brave://extensions`.
2. Enable **Developer mode**.
3. Choose **Load unpacked** and select this `browser-extension` folder.
4. Reload an open YouTube watch tab once.

For Chrome, use `chrome://extensions` and the same steps.

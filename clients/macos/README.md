# macOS companion scaffold

This Swift 6 package proves the macOS media-provider boundary for Apple Music and Spotify using fixed AppleScript queries and commands.

It currently supports:

- `probe` — emits the MASHR `/api/now` snapshot as JSON.
- `command play|pause|stop|next|previous|back10|forward10` — invokes one fixed media action.
- `--help` — reports status and requirements.

The first query/control may trigger macOS's normal Automation permission prompt for Music or Spotify. No network listener, discovery socket, login item, or menu-bar process is created. The scaffold therefore cannot pair with Android yet.

Do not enable LAN access until the controls sit behind the complete contract in [`../PROTOCOL.md`](../PROTOCOL.md): loopback default, explicit LAN mode, paired-phone source filtering, one-time key exchange, HMAC verification, nonce replay rejection, and rate limits.


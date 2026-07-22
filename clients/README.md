# Desktop companion ports

The Windows companion remains the complete, owner-tested implementation. These Linux and macOS folders are buildable contributor scaffolds for porting the PC side of MASHR Media Deck without cloning Windows-specific code into the Android app.

| Host | Provider scaffold | Phone listener | Pairing/HMAC | Status |
| --- | --- | --- | --- | --- |
| Windows 10/11 | Windows media sessions + VLC fallback | Yes | Yes | Complete developer preview |
| Linux | MPRIS through `playerctl` | No | Contract only | Buildable scaffold |
| macOS | Music/Spotify through AppleScript | No | Contract only | Buildable scaffold |

The scaffolds intentionally do **not** open TCP or UDP ports. They currently expose a `probe` command that emits the same media snapshot shape used by `/api/now`, plus a small allowlisted control command. A future port must implement the security and network gates in [PROTOCOL.md](PROTOCOL.md) before enabling phone access.

## Try the provider adapters

Linux:

```bash
dotnet run --project clients/linux/Mashr.MediaDeck.Linux -- probe
dotnet run --project clients/linux/Mashr.MediaDeck.Linux -- command pause
```

macOS:

```bash
swift run --package-path clients/macos mashr-media-deck-mac probe
swift run --package-path clients/macos mashr-media-deck-mac command pause
```

Both adapters use fixed command allowlists. Neither accepts shell fragments, window handles, key codes, or arbitrary AppleScript/playerctl arguments from a caller.

## Porting milestones

1. Keep the provider adapter separate from the transport.
2. Match the snapshot and command behavior in [PROTOCOL.md](PROTOCOL.md).
3. Add local key storage, one-time pairing, HMAC verification, nonce replay rejection, and rate limits.
4. Bind to loopback by default. Add explicit paired-phone and same-subnet modes only after the security layer is tested.
5. Add platform-native tray/menu-bar status and startup integration.
6. Validate the Android app against the port, then promote it from scaffold status.


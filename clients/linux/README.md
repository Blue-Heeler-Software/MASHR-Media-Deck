# Linux companion scaffold

This .NET 10 console scaffold proves the Linux media-provider boundary using the standard MPRIS surface through `playerctl`.

It currently supports:

- `probe` — emits the MASHR `/api/now` snapshot as JSON.
- `command play|pause|stop|next|previous|back10|forward10` — invokes one fixed `playerctl` action.
- `--help` — reports status and requirements.

It intentionally starts no HTTP or UDP listener, so it cannot pair with the Android app yet. Do not add a `0.0.0.0` bind as a shortcut. Implement the pairing, signed-request, nonce, rate-limit, and source-IP gates in [`../PROTOCOL.md`](../PROTOCOL.md) first.

Requirements: a Linux desktop with an MPRIS-capable player, `playerctl`, and .NET 10 SDK/runtime.


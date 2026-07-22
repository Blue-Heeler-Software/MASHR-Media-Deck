# MASHR Media Deck companion protocol

This document captures the interoperability boundary implemented by the Windows reference companion. Linux and macOS ports should match it before they enable a LAN listener.

## Transport and discovery

- HTTP companion port: TCP `43821`.
- Optional discovery port: UDP `43822`.
- Discovery request: the exact UTF-8 payload `MEDIADECK_DISCOVER`.
- Discovery reply: `MEDIADECK:43821`.
- Discovery and LAN HTTP are off by default. Loopback is the default bind.
- A LAN-enabled implementation must reject callers outside its configured paired-phone address or directly connected subnet before authentication is evaluated.

This protocol provides authenticated integrity and replay resistance, not encrypted LAN confidentiality. Do not port-forward these ports or expose them to the internet.

## Pairing and signed requests

`POST /api/pair` carries a six-digit one-time code in `X-MediaDeck-Pairing-Code`. A successful response returns a random 32-byte key encoded as Base64:

```json
{
  "key": "<base64>",
  "algorithm": "HMAC-SHA256",
  "clockWindowSeconds": 30
}
```

Every protected request includes:

- `X-MediaDeck-Time`: current Unix time in seconds.
- `X-MediaDeck-Nonce`: 16–64 ASCII alphanumeric characters, never reused.
- `X-MediaDeck-Signature`: Base64-encoded HMAC-SHA256.

The signed UTF-8 canonical value is:

```text
UPPERCASE_METHOD\n
EXACT_PATH_AND_QUERY\n
UNIX_TIMESTAMP\n
NONCE
```

The reference companion accepts a 30-second clock window, compares signatures in constant time, and rejects reused nonces. The request body is not part of the current signature, so state-changing inputs must remain bounded query parameters or fixed allowlisted actions until a versioned protocol adds body hashing.

Unauthenticated routes are limited to `/`, `/api/health`, and `/api/pair`. Browser-helper routes are loopback-only and are not part of the cross-platform core.

## Core snapshot

`GET /api/now` returns the selected media state:

```json
{
  "title": "Track or video title",
  "artist": "Artist or channel",
  "source": "platform provider identifier",
  "playing": true,
  "positionMs": 21000,
  "durationMs": 1958000,
  "shuffle": false,
  "repeat": "none",
  "youtubeAvailable": false,
  "youtubeVolume": -1,
  "chapters": [],
  "instantReplayAvailable": false,
  "instantReplayEnabled": false,
  "instantReplaySeconds": 0
}
```

Providers should use `0` when a timeline is unavailable, `-1` when YouTube player volume is unavailable, and an empty chapter list when no bounded creator chapters can be verified.

## Core routes

| Method and path | Purpose |
| --- | --- |
| `GET /api/health` | Service name, pairing state, LAN mode, and bind address |
| `POST /api/pair` | Exchange the one-time local code for the HMAC key |
| `GET /api/now` | Selected session snapshot |
| `GET /api/art` | Current artwork bytes, or `404` |
| `GET /api/sessions` | Available sessions and selected session |
| `POST /api/sessions/select?source=...` | Select an exact provider-owned source identifier |
| `POST /api/seek?positionMs=...` | Seek within provider-advertised bounds |
| `POST /api/skip?seconds=-120..120` | Skip by a bounded, non-zero signed duration |
| `POST /api/control/{command}` | Invoke one fixed allowlisted action |
| `POST /api/youtube/volume?level=0..100` | Set the selected YouTube player's own volume |

Baseline control names are `play`, `pause`, `stop`, `next`, `previous`, `back10`, `forward10`, `shuffle`, `repeat`, `mute`, `volumedown`, and `volumeup`. `back10` and `forward10` remain fixed compatibility commands; new clients should use bounded `POST /api/skip` for a user-selected skip duration. Windows-only controls currently include `movescreen`, `screenshot`, `alttab`, `altdown`, `altup`, `arrowleft`, `arrowright`, `instantreplay`, and `replayarm`. YouTube-specific controls are `like`, `dislike`, and `subscribe`.

A port must return an explicit non-success result for an unsupported action. It must never reinterpret an unknown command as a shell command, key sequence, AppleScript fragment, D-Bus member, process name, or window identifier.

## Required security gates before LAN support

- Loopback-only default with no discovery listener.
- Explicit LAN enablement and a visible status indicator.
- Paired-phone source-IP allowlisting, with same-subnet mode labeled as broader exposure.
- One active pairing code, rotation on reset, and private per-user key storage.
- HMAC verification, 30-second freshness, nonce replay rejection, and constant-time comparison.
- Request/header/body limits and per-source rate limiting; pairing needs a stricter limiter.
- Exact endpoint and command allowlists.
- No router port forwards, wildcard public bind, arbitrary input injection, or browser cookies.

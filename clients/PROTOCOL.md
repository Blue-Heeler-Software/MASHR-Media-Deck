# MASHR Media Deck companion protocol

This document captures the interoperability boundary implemented by the Windows reference companion. Linux and macOS ports should match it before they enable a LAN listener.

## Transport and discovery

- HTTP companion port: TCP `43821`.
- Optional discovery port: UDP `43822`.
- Discovery request: the exact UTF-8 payload `MEDIADECK_DISCOVER`.
- Discovery reply: `MEDIADECK:43821`.
- A LAN-enabled reference companion also broadcasts that reply once per second on UDP `43822`. This permits discovery while the inbound firewall remains scoped to the PC's exact unicast address; clients should ignore their own broadcast request and unrelated datagrams.
- Discovery and LAN HTTP are off by default. Loopback is the default bind.
- A LAN-enabled implementation must reject callers outside its configured paired-phone address or directly connected subnet before authentication is evaluated.

This protocol provides authenticated integrity and replay resistance, not encrypted LAN confidentiality. Do not port-forward these ports or expose them to the internet.

## Pairing and signed requests

`POST /api/pair/nearby` is the default pairing path. The controller sends a persistent random ID in `X-MediaDeck-Device`, a bounded display name in `X-MediaDeck-Device-Name`, a fresh 32–64 character random token in `X-MediaDeck-Pairing-Request`, and an X.509 SubjectPublicKeyInfo-encoded 2048–4096 bit RSA key in `X-MediaDeck-Pairing-Public-Key`. The companion returns `202 waiting` and displays the request's name and source IP. After one local dashboard click, the exact same ID, token, public key, and source IP may call the route again within 30 seconds. The response contains `wrappedKey`, an RSA-OAEP-SHA256 encryption of the 32-byte HMAC key; the raw key is never sent by this route. Claims are atomic and one-use.

`POST /api/pair` is the fallback path. It carries a six-digit code in `X-MediaDeck-Pairing-Code` plus the same ID and name headers. The reference companion accepts the code only during an explicit two-minute window and returns a distinct random 32-byte key encoded as Base64:

```json
{
  "key": "<base64>",
  "deviceId": "<persistent-controller-id>",
  "algorithm": "HMAC-SHA256",
  "clockWindowSeconds": 30
}
```

The nearby response instead uses `wrappedKey` and reports `RSA-OAEP-SHA256+HMAC-SHA256`; clients decrypt it with the private half of the ephemeral request key before storing the resulting 32-byte HMAC key.

Every protected request includes:

- `X-MediaDeck-Device`: the persistent controller ID returned or accepted during pairing.
- `X-MediaDeck-Device-Name`: a bounded display name used only by the local PC roster.
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

The reference companion looks up the per-device key, accepts a 30-second clock window, compares signatures in constant time, and rejects reused nonces in that device's namespace. Revoking a roster entry invalidates only that controller. The request body is not part of the current signature, so state-changing inputs must remain bounded query parameters or fixed allowlisted actions until a versioned protocol adds body hashing.

Unauthenticated routes are limited to `/`, `/api/health`, `/api/pair`, and `/api/pair/nearby`. The nearby route performs no control action, has its own per-source limiter, and releases a key only after an expiring local approval bound to the request token and source IP. Browser-helper routes are loopback-only and are not part of the cross-platform core.

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
  "youtubeJumpAheadEligible": false,
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
| `POST /api/pair/nearby` | Register or claim an expiring, locally approved nearby request |
| `POST /api/pair` | Exchange the one-time local code for the HMAC key |
| `GET /api/now` | Selected session snapshot |
| `GET /api/art` | Current artwork bytes, or `404` |
| `GET /api/sessions` | Available sessions and selected session |
| `POST /api/sessions/select?source=...` | Select an exact provider-owned source identifier |
| `POST /api/seek?positionMs=...` | Seek within provider-advertised bounds |
| `POST /api/skip?seconds=-120..120` | Skip by a bounded, non-zero signed duration |
| `POST /api/control/{command}` | Invoke one fixed allowlisted action |
| `POST /api/youtube/volume?level=0..100` | Set the selected YouTube player's own volume |
| `POST /api/youtube/jumpahead` | Ask the verified signed-in YouTube watch player to use its Premium Jump Ahead marker; `409` when unavailable |

Baseline control names are `play`, `pause`, `stop`, `next`, `previous`, `back10`, `forward10`, `shuffle`, `repeat`, `mute`, `volumedown`, and `volumeup`. `back10` and `forward10` remain fixed compatibility commands; new clients should use bounded `POST /api/skip` for a user-selected skip duration. Windows-only controls currently include `movescreen`, `screenshot`, `alttab`, `altdown`, `altup`, `arrowleft`, `arrowright`, `instantreplay`, and `replayarm`. YouTube-specific controls are `like`, `dislike`, and `subscribe`.

A port must return an explicit non-success result for an unsupported action. It must never reinterpret an unknown command as a shell command, key sequence, AppleScript fragment, D-Bus member, process name, or window identifier.

## Required security gates before LAN support

- Loopback-only default with no discovery listener.
- Explicit LAN enablement and a visible status indicator.
- Paired-phone source-IP allowlisting, with same-subnet mode labeled as broader exposure.
- Expiring nearby requests, explicit local approval, one-use token/IP-bound claims, fallback-code rotation, and private per-user key storage.
- HMAC verification, 30-second freshness, nonce replay rejection, and constant-time comparison.
- Request/header/body limits and per-source rate limiting; pairing needs a stricter limiter.
- Exact endpoint and command allowlists.
- No router port forwards, wildcard public bind, arbitrary input injection, or browser cookies.

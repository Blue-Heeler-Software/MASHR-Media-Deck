# MASHR Media Deck companion protocol

This document captures the interoperability boundary implemented by the Windows reference companion. Linux and macOS ports should match it before they enable a LAN listener.

## Transport and discovery

- HTTP companion port: TCP `43821`.
- Optional discovery port: UDP `43822`.
- Discovery request: the exact UTF-8 payload `MEDIADECK_DISCOVER`.
- Discovery reply: `MEDIADECK:43821`.
- During an explicit pairing window, a LAN-enabled reference companion also broadcasts that reply once per second on UDP `43822`. Outside pairing, it only answers directed discovery requests from callers allowed by the configured LAN scope. This permits reconnection while the inbound firewall remains scoped to the PC's exact unicast address; clients should ignore their own broadcast request and unrelated datagrams.
- Discovery and LAN HTTP are off by default. Loopback is the default bind.
- A LAN-enabled implementation must reject callers outside its configured exact paired-phone address list or directly connected subnet before authentication is evaluated. Paired-phone addresses may be reached through existing routed private-LAN paths; this does not widen the exact source-IP allowlist. Broadcast discovery is not expected to cross those routes, so clients must retain a manually entered or previously discovered companion address.

This protocol provides authenticated integrity and replay resistance, not encrypted LAN confidentiality. Do not port-forward these ports or expose them to the internet.

## Pairing and signed requests

`POST /api/pair/nearby` is the only key-granting pairing path. It is accepted only during an explicit two-minute PC pairing window. The controller sends `X-MediaDeck-Pairing-Protocol: 2`, a persistent random ID in `X-MediaDeck-Device`, a bounded display name in `X-MediaDeck-Device-Name`, a fresh 32–64 character random token in `X-MediaDeck-Pairing-Request`, and an X.509 SubjectPublicKeyInfo-encoded 2048–4096 bit RSA key in `X-MediaDeck-Pairing-Public-Key`. Older pairing clients receive `426 Upgrade Required`. The companion allows at most two pending requests per source address and returns `202 waiting` with its persistent 3072-bit RSA public key and a six-digit `verificationCode`.

The phone and PC derive that code from SHA-256 over the versioned transcript, persistent device ID, fresh token, exact phone public key, and exact PC public key. The user must compare the code on both screens. After one local dashboard click, the exact same ID, token, public key, and source IP may call the route again within 30 seconds. The response contains:

- `wrappedKey`: RSA-OAEP-SHA256 encryption of the distinct random 32-byte HMAC key to the phone key.
- `pcSignature`: RSA-PSS-SHA256 over the request ID, exact wrapped key, and grant expiry.
- `pcPublicKey`, `verificationCode`, `requestId`, and `grantExpiresUnix` for transcript verification.

The phone verifies the code and PC signature before decrypting or storing the key. Claims are atomic and one-use. `POST /api/pair` is retained only as an explicit `410 Gone` compatibility response; the former cleartext fallback exchange cannot release a key.

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

Protected requests with a body or transfer encoding are rejected. Every protected response includes `X-MediaDeck-Response-Nonce` and `X-MediaDeck-Response-Signature`. The signature is HMAC-SHA256 over `RESPONSE`, status code, exact path/query, the request nonce, and the lowercase SHA-256 response-body hash. Clients must compare the echoed nonce and verify that signature before parsing or displaying the response.

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
  "instantReplaySeconds": 0,
  "microphoneAvailable": true,
  "microphoneMuted": false
}
```

Providers should use `0` when a timeline is unavailable, `-1` when YouTube player volume is unavailable, and an empty chapter list when no bounded creator chapters can be verified.

## Core routes

| Method and path | Purpose |
| --- | --- |
| `GET /api/health` | Service name, pairing state, LAN mode, and bind address |
| `POST /api/pair/nearby` | Register or claim an expiring, locally approved nearby request |
| `POST /api/pair` | Retired compatibility route; always returns `410 Gone` |
| `GET /api/now` | Selected session snapshot |
| `GET /api/art` | Current artwork bytes, or `404` |
| `GET /api/sessions` | Available sessions and selected session |
| `POST /api/sessions/select?source=...` | Select an exact provider-owned source identifier |
| `POST /api/seek?positionMs=...` | Seek within provider-advertised bounds |
| `POST /api/skip?seconds=-120..120` | Skip by a bounded, non-zero signed duration |
| `POST /api/pointer?dx=-300..300&dy=-300..300` | Move the Windows pointer by a bounded relative delta; at least one axis must be non-zero |
| `POST /api/control/{command}` | Invoke one fixed allowlisted action |
| `POST /api/youtube/volume?level=0..100` | Set the selected YouTube player's own volume |
| `POST /api/youtube/jumpahead` | Ask the verified signed-in YouTube watch player to use its Premium Jump Ahead marker; `409` when unavailable |

Baseline control names are `play`, `pause`, `stop`, `next`, `previous`, `back10`, `forward10`, `shuffle`, `repeat`, `mute`, `volumedown`, and `volumeup`. `back10` and `forward10` remain fixed compatibility commands; new clients should use bounded `POST /api/skip` for a user-selected skip duration. Windows-only controls currently include `movescreen`, `screenshot`, `alttab`, `altdown`, `altup`, `arrowleft`, `arrowright`, `instantreplay`, `replayarm`, and `micmute`. YouTube-specific controls are `like`, `dislike`, and `subscribe`.

A port must return an explicit non-success result for an unsupported action. It must never reinterpret an unknown command as a shell command, key sequence, AppleScript fragment, D-Bus member, process name, or window identifier.

## Required security gates before LAN support

- Loopback-only default with no discovery listener.
- Explicit LAN enablement and a visible status indicator.
- Exact paired-phone source-IP allowlisting, with same-subnet mode labeled as broader exposure.
- Explicit pairing windows, expiring nearby requests, per-address pending caps, matching transcript codes, local approval, one-use token/IP-bound claims, PC-signed grants, and protected per-user key storage.
- HMAC verification, 30-second freshness, nonce replay rejection, and constant-time comparison.
- Request/header/body limits and per-source rate limiting; pairing needs a stricter limiter.
- Exact endpoint and command allowlists.
- No router port forwards, wildcard public bind, arbitrary input injection, or browser cookies.

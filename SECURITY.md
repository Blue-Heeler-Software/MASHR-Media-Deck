# MASHR Media Deck security notes

## What is protected

MASHR Media Deck control and metadata requests require an HMAC-SHA256 signature made with a per-device random 256-bit key. The signature covers the HTTP method, exact path and query, timestamp, and random nonce. The companion selects the paired device key by a random persistent device ID, rejects stale timestamps, rejects already-seen nonces, and rejects authenticated request bodies. Every authenticated response is also bound to the exact request nonce, status, path/query, and response-body hash with HMAC-SHA256, so an interceptor cannot silently replace or replay media state.

An unpaired phone may submit a bounded nearby request from an allowed LAN address only while an explicit two-minute pairing window is open. That request contains a persistent device ID, fresh random claim token, and ephemeral 2048-bit RSA public key; it expires after 45 seconds and grants no control authority by itself. The PC has a persistent 3072-bit RSA identity. Both sides derive the same six-digit **MATCH** code from the device ID, token, phone key, and PC key. A local user must compare that code and click **PAIR THIS DEVICE** beside the expected name and source IP. The resulting grant expires after 30 seconds, can be claimed once, is bound to the exact request token, public key, and source IP, and is signed by the PC identity with RSA-PSS-SHA256. The 256-bit HMAC key is encrypted with RSA-OAEP-SHA256 to that phone before it crosses the LAN. The old fallback endpoint now returns `410 Gone` and never releases a raw key.

Android wraps the controller key with a non-exportable Android Keystore AES-GCM key and excludes preferences from backup and device-to-device transfer. Windows protects stored controller keys and its PC identity with current-user DPAPI. Existing plaintext Windows and Android key records are migrated in place on first successful load.

LAN access is off by default. The companion then binds only to `127.0.0.1` and starts no UDP discovery listener. Enabling LAN access requires an explicit route-validated scope created by the configuration script.

In the recommended `PairedPhone` mode, the companion follows Windows' existing route to each explicitly configured RFC1918 phone address and binds only to loopback and the required exact PC interface addresses. Phones may be on different routed private subnets. Both the application middleware and Windows Firewall accept LAN traffic only from the listed exact IPv4 addresses. The firewall rules are additionally scoped to the active Windows profiles, companion executable, exact local addresses/interfaces, TCP port `43821`, and UDP port `43822`. This exact-IP scope is permitted on a Public, Private, or domain profile because it does not admit the rest of the network.

`SameSubnet` mode accepts the exact calculated directly connected subnet CIDR and is required for discovery/pairing of new phone addresses without another administrator firewall edit. The script refuses this broader mode unless that interface is classified Private by Windows. This is not protection against an untrusted laptop already on that LAN: that laptop can reach the bounded HTTP parser. It can submit requests only during an explicit pairing window, receives at most two pending slots per address, receives no key unless the local PC user approves the exact displayed request after comparing the MATCH code, and cannot authenticate a control request without a device key. Paired-phone mode remains the strict one-device default.

Browser endpoints additionally require loopback. All inputs are allowlisted or length-limited, and video playback accepts only an ID from the companion's current nine-item recommendation set. A source-IP restriction is defense in depth rather than device identity: IP spoofing is possible, so HMAC pairing remains mandatory.

Extension-free YouTube chapters use Windows UI Automation only to read the address bar of the unambiguous selected Brave, Chrome, or Edge media window. The companion accepts only an exact `youtube.com/watch` or `youtu.be` URL with an 11-character video ID, constructs its own public `youtube.com` request, sends no browser cookies, caps the response at 2 MiB, and extracts only bounded timestamp/title pairs.

YouTube Like, Dislike, and Subscribe commands use the same unambiguous selected browser window and first verify an exact `youtube.com` host in its address bar. They can activate only visible, enabled accessibility buttons with tightly matched YouTube labels. The API accepts no coordinates, DOM selector, text, URL, or arbitrary click target from the phone. Subscribe is one-way: an already-subscribed channel reports its state instead of exposing Unsubscribe.

YouTube volume accepts only an integer from 0 through 100. The companion locates the exact enabled accessibility slider named `Volume`, derives its Chromium render-host ancestor and current position locally, then sends only the fixed left/right adjustment needed to reach the requested level. The phone cannot provide a window handle, coordinate, key code, or arbitrary accessibility label; the physical cursor and Windows master volume are not changed.

YouTube Premium embedded-segment skipping accepts no timestamp, key code, window handle, URL, or selector from the phone. The companion first verifies the unambiguous selected Chromium window has an exact `youtube.com/watch` URL. It invokes a visible accessibility button beginning with `Jump ahead` when present, otherwise sends only YouTube's fixed Windows `Ctrl+Right` Jump Ahead chord to that verified page's visible Chromium render host. It verifies that the media timeline actually advanced; failure returns `409` so the phone can use its bounded configured skip.

NVIDIA replay controls read the current user's local NVIDIA Overlay settings. Hotkeys are sourced from NVIDIA's `DVRSave` and `DVRToggle` arrays, limited to four valid virtual-key codes, and never supplied by the phone. The arm endpoint is one-way: if replay is already enabled it does not toggle it off.

Microphone mute is one fixed authenticated `micmute` command with no caller-supplied device ID, endpoint name, volume, process, or executable. The Windows companion enumerates active capture endpoints locally through Core Audio, reads their real mute state for every deck refresh, and can only invert that bounded mute state.

The monitor-switch command accepts no process ID, window handle, coordinates, or executable name from the phone. The companion derives an allowlisted player process from the authenticated Windows media session, requires an unambiguous top-level media window, and moves it without activation or Z-order changes.

Pointer control is off by default on the phone. When **Pad Controls Pointer** is enabled, only drags beginning on unused deck surfaces produce movement; buttons, sliders, artwork, replay, microphone mute, and multitouch gestures keep their dedicated handlers. The signed endpoint accepts only relative integer deltas from -300 through 300 per axis, rejects zero movement, generates no clicks or keyboard input, and has a separate per-source rate partition.

## Remaining limitation

The LAN transport is HTTP rather than TLS. Request and response HMACs prevent an observer from forging or replaying controls or media state after pairing, and nearby pairing encrypts the device key to the phone's ephemeral RSA key. Media titles, artwork, paths, status codes, and traffic timing are still not confidential. First contact is authenticated only when the user actually compares the independently derived MATCH code before approving; ignoring a mismatch defeats that protection. An ordinary WLAN client that cannot intercept another device's traffic gains no key merely by reaching the listener.

For the intended home-LAN setup, pair on a WPA2/WPA3 private network, do not pair on guest/public Wi-Fi, and do not create router port forwards. The configuration scripts create active-profile firewall rules: an explicit list of up to sixteen exact phone addresses for `PairedPhone`, or a directly connected Private-profile subnet for `SameSubnet`. A later TLS iteration would still be needed for confidentiality against an already-compromised LAN.

## Linux and macOS scaffold boundary

The Linux and macOS provider scaffolds under `clients/` start no TCP or UDP listener and cannot pair with the Android app. They are deliberately loopback/non-network building blocks until the HMAC pairing, nonce replay rejection, source-IP policy, request limits, and rate limits in [`clients/PROTOCOL.md`](clients/PROTOCOL.md) are implemented and tested. Do not expose either provider by wrapping it in an unauthenticated LAN service.

## If a phone or key may be compromised

Open the MASHR Media Deck dashboard, select the affected controller, and choose **REVOKE SELECTED**. Its key stops authenticating immediately while other phones keep working. If device identity is uncertain, choose **FORGET ALL**, then pair the intended controllers again during the fresh two-minute window.

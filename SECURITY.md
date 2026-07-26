# MASHR Media Deck security notes

## What is protected

MASHR Media Deck control and metadata requests require an HMAC-SHA256 signature made with a per-device random 256-bit key stored in the private app data of that phone and the current Windows user. The signature covers the HTTP method, exact path and query, timestamp, and random nonce. The companion selects the paired device key by a random persistent device ID, rejects stale timestamps, and rejects already-seen nonces.

An unpaired phone may submit a bounded nearby request from an allowed LAN address. That request contains a persistent device ID, fresh random claim token, and ephemeral 2048-bit RSA public key; it expires after 45 seconds and grants no control authority by itself. A local user must click **PAIR THIS DEVICE** beside the expected name and source IP. The resulting grant expires after 30 seconds, can be claimed once, and is bound to the exact request token, public key, and source IP. The 256-bit HMAC key is encrypted with RSA-OAEP-SHA256 to that phone before it crosses the LAN, and only the ephemeral Android private key can decrypt it. The six-digit pairing code remains available only during an explicit two-minute fallback window.

LAN access is off by default. The companion then binds only to `127.0.0.1` and starts no UDP discovery listener. Enabling LAN access does not require changing the Windows network profile.

In the recommended `PairedPhone` mode, the companion binds only to loopback and the one PC interface address that shares a directly connected subnet with the configured phone. Both the application middleware and Windows Firewall accept LAN traffic only from that phone's IPv4 address. The firewall rules are additionally scoped to the companion executable, exact local address, interface, TCP port `43821`, and UDP port `43822`.

`SameSubnet` mode binds the same way but accepts the exact calculated subnet CIDR and is required for discovery/pairing of new phone addresses without another administrator firewall edit. This is not protection against an untrusted laptop already on that WLAN: that laptop can reach the bounded HTTP parser and can display a short-lived request in the dashboard. It receives no key unless the local PC user approves that exact displayed request, and it cannot authenticate a control request without a device key. Confirm both device name and IP before clicking; paired-phone mode remains the strict one-device default.

Browser endpoints additionally require loopback. All inputs are allowlisted or length-limited, and video playback accepts only an ID from the companion's current nine-item recommendation set. A source-IP restriction is defense in depth rather than device identity: IP spoofing is possible, so HMAC pairing remains mandatory.

Extension-free YouTube chapters use Windows UI Automation only to read the address bar of the unambiguous selected Brave, Chrome, or Edge media window. The companion accepts only an exact `youtube.com/watch` or `youtu.be` URL with an 11-character video ID, constructs its own public `youtube.com` request, sends no browser cookies, caps the response at 2 MiB, and extracts only bounded timestamp/title pairs.

YouTube Like, Dislike, and Subscribe commands use the same unambiguous selected browser window and first verify an exact `youtube.com` host in its address bar. They can activate only visible, enabled accessibility buttons with tightly matched YouTube labels. The API accepts no coordinates, DOM selector, text, URL, or arbitrary click target from the phone. Subscribe is one-way: an already-subscribed channel reports its state instead of exposing Unsubscribe.

YouTube volume accepts only an integer from 0 through 100. The companion locates the exact enabled accessibility slider named `Volume`, derives its Chromium render-host ancestor and current position locally, then sends only the fixed left/right adjustment needed to reach the requested level. The phone cannot provide a window handle, coordinate, key code, or arbitrary accessibility label; the physical cursor and Windows master volume are not changed.

YouTube Premium embedded-segment skipping accepts no timestamp, key code, window handle, URL, or selector from the phone. The companion first verifies the unambiguous selected Chromium window has an exact `youtube.com/watch` URL. It invokes a visible accessibility button beginning with `Jump ahead` when present, otherwise sends only YouTube's fixed Windows `Ctrl+Right` Jump Ahead chord to that verified page's visible Chromium render host. It verifies that the media timeline actually advanced; failure returns `409` so the phone can use its bounded configured skip.

NVIDIA replay controls read the current user's local NVIDIA Overlay settings. Hotkeys are sourced from NVIDIA's `DVRSave` and `DVRToggle` arrays, limited to four valid virtual-key codes, and never supplied by the phone. The arm endpoint is one-way: if replay is already enabled it does not toggle it off.

The monitor-switch command accepts no process ID, window handle, coordinates, or executable name from the phone. The companion derives an allowlisted player process from the authenticated Windows media session, requires an unambiguous top-level media window, and moves it without activation or Z-order changes.

## Remaining limitation

The LAN transport is HTTP rather than TLS. HMAC prevents an observer from forging or replaying control requests after pairing, and the default nearby flow encrypts the device key to the phone's ephemeral RSA key, but media titles, artwork, and ordinary responses are not confidential. The initial public-key request has no previously shared trust anchor, so an attacker capable of actively intercepting and modifying that first exchange could substitute a key and cause denial or attempt a man-in-the-middle attack. Fully authenticating first contact against that threat requires an out-of-band QR/fingerprint or code comparison; an ordinary WLAN client that cannot intercept another device's traffic gains no key merely by reaching the listener.

For the intended home-LAN setup, pair on a WPA2/WPA3 private network, do not pair on guest/public Wi-Fi, do not create router port forwards, and keep any Windows Firewall rule limited to the Private profile. A later certificate-pinning/QR-pairing iteration would be needed for confidentiality against an already-compromised LAN.

## Linux and macOS scaffold boundary

The Linux and macOS provider scaffolds under `clients/` start no TCP or UDP listener and cannot pair with the Android app. They are deliberately loopback/non-network building blocks until the HMAC pairing, nonce replay rejection, source-IP policy, request limits, and rate limits in [`clients/PROTOCOL.md`](clients/PROTOCOL.md) are implemented and tested. Do not expose either provider by wrapping it in an unauthenticated LAN service.

## If a phone or key may be compromised

Open the MASHR Media Deck dashboard, select the affected controller, and choose **REVOKE SELECTED**. Its key stops authenticating immediately while other phones keep working. If device identity is uncertain, choose **FORGET ALL**, then pair the intended controllers again during the fresh two-minute window.

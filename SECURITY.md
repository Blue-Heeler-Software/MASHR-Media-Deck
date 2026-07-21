# MASHR Media Deck security notes

## What is protected

MASHR Media Deck control and metadata requests require an HMAC-SHA256 signature made with a random 256-bit key stored in the private app data of the phone and the current Windows user. The signature covers the HTTP method, exact path and query, timestamp, and random nonce. The companion rejects stale timestamps and already-seen nonces.

The six-digit pairing code is valid only while pairing is open. A successful pairing closes it. **Reset phone pairing** rotates the device key before opening a new code. Pairing attempts are rate-limited.

LAN access is off by default. The companion then binds only to `127.0.0.1` and starts no UDP discovery listener. Enabling LAN access does not require changing the Windows network profile.

In the recommended `PairedPhone` mode, the companion binds only to loopback and the one PC interface address that shares a directly connected subnet with the configured phone. Both the application middleware and Windows Firewall accept LAN traffic only from that phone's IPv4 address. The firewall rules are additionally scoped to the companion executable, exact local address, interface, TCP port `43821`, and UDP port `43822`.

`SameSubnet` mode binds the same way but accepts the exact calculated subnet CIDR. This is not protection against an untrusted laptop already on that WLAN; that laptop can reach the HTTP parser, although it still cannot authenticate a control request without the random device key. Paired-phone mode therefore remains the default.

Browser endpoints additionally require loopback. All inputs are allowlisted or length-limited, and video playback accepts only an ID from the companion's current nine-item recommendation set. A source-IP restriction is defense in depth rather than device identity: IP spoofing is possible, so HMAC pairing remains mandatory.

Extension-free YouTube chapters use Windows UI Automation only to read the address bar of the unambiguous selected Brave, Chrome, or Edge media window. The companion accepts only an exact `youtube.com/watch` or `youtu.be` URL with an 11-character video ID, constructs its own public `youtube.com` request, sends no browser cookies, caps the response at 2 MiB, and extracts only bounded timestamp/title pairs.

YouTube Like, Dislike, and Subscribe commands use the same unambiguous selected browser window and first verify an exact `youtube.com` host in its address bar. They can activate only visible, enabled accessibility buttons with tightly matched YouTube labels. The API accepts no coordinates, DOM selector, text, URL, or arbitrary click target from the phone. Subscribe is one-way: an already-subscribed channel reports its state instead of exposing Unsubscribe.

NVIDIA replay controls read the current user's local NVIDIA Overlay settings. Hotkeys are sourced from NVIDIA's `DVRSave` and `DVRToggle` arrays, limited to four valid virtual-key codes, and never supplied by the phone. The arm endpoint is one-way: if replay is already enabled it does not toggle it off.

The monitor-switch command accepts no process ID, window handle, coordinates, or executable name from the phone. The companion derives an allowlisted player process from the authenticated Windows media session, requires an unambiguous top-level media window, and moves it without activation or Z-order changes.

## Remaining limitation

The LAN transport is HTTP rather than TLS. HMAC prevents an observer from forging or replaying control requests after pairing, but it does not hide media titles, artwork, or response contents. An attacker who can actively sniff the exact one-time pairing exchange could also capture the returned device key.

For the intended home-LAN setup, pair on a WPA2/WPA3 private network, do not pair on guest/public Wi-Fi, do not create router port forwards, and keep any Windows Firewall rule limited to the Private profile. A later certificate-pinning/QR-pairing iteration would be needed for confidentiality against an already-compromised LAN.

## If a phone or key may be compromised

Right-click the MASHR Media Deck tray shield and select **Reset phone pairing**. This invalidates the old phone key immediately. Pair the intended phone again with the new one-time code.

# MediaDeck security notes

## What is protected

MediaDeck control and metadata requests require an HMAC-SHA256 signature made with a random 256-bit key stored in the private app data of the phone and the current Windows user. The signature covers the HTTP method, exact path and query, timestamp, and random nonce. The companion rejects stale timestamps and already-seen nonces.

The six-digit pairing code is valid only while pairing is open. A successful pairing closes it. **Reset phone pairing** rotates the device key before opening a new code. Pairing attempts are rate-limited.

The companion accepts LAN calls only from RFC1918 IPv4, IPv4 link-local, IPv6 unique-local/link-local, or loopback addresses. Browser endpoints additionally require loopback. All inputs are allowlisted or length-limited, and video playback accepts only an ID from the companion's current nine-item recommendation set.

The companion queries Windows Network List Manager before binding. On a Public network profile it binds only to `127.0.0.1`, starts no discovery listener, and therefore cannot accept phone/LAN traffic. If an initially Private network changes to Public while the companion is running, it shuts down.

## Remaining limitation

The LAN transport is HTTP rather than TLS. HMAC prevents an observer from forging or replaying control requests after pairing, but it does not hide media titles, artwork, or response contents. An attacker who can actively sniff the exact one-time pairing exchange could also capture the returned device key.

For the intended home-LAN setup, pair on a WPA2/WPA3 private network, do not pair on guest/public Wi-Fi, do not create router port forwards, and keep any Windows Firewall rule limited to the Private profile. A later certificate-pinning/QR-pairing iteration would be needed for confidentiality against an already-compromised LAN.

## If a phone or key may be compromised

Right-click the MediaDeck tray shield and select **Reset phone pairing**. This invalidates the old phone key immediately. Pair the intended phone again with the new one-time code.

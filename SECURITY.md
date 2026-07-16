# MediaDeck security notes

## What is protected

MediaDeck control and metadata requests require an HMAC-SHA256 signature made with a random 256-bit key stored in the private app data of the phone and the current Windows user. The signature covers the HTTP method, exact path and query, timestamp, and random nonce. The companion rejects stale timestamps and already-seen nonces.

The six-digit pairing code is valid only while pairing is open. A successful pairing closes it. **Reset phone pairing** rotates the device key before opening a new code. Pairing attempts are rate-limited.

LAN access is off by default. The companion then binds only to `127.0.0.1` and starts no UDP discovery listener. Enabling LAN access does not require changing the Windows network profile.

In the recommended `PairedPhone` mode, the companion binds only to loopback and the one PC interface address that shares a directly connected subnet with the configured phone. Both the application middleware and Windows Firewall accept LAN traffic only from that phone's IPv4 address. The firewall rules are additionally scoped to the companion executable, exact local address, interface, TCP port `43821`, and UDP port `43822`.

`SameSubnet` mode binds the same way but accepts the exact calculated subnet CIDR. This is not protection against an untrusted laptop already on that WLAN; that laptop can reach the HTTP parser, although it still cannot authenticate a control request without the random device key. Paired-phone mode therefore remains the default.

Browser endpoints additionally require loopback. All inputs are allowlisted or length-limited, and video playback accepts only an ID from the companion's current nine-item recommendation set. A source-IP restriction is defense in depth rather than device identity: IP spoofing is possible, so HMAC pairing remains mandatory.

## Remaining limitation

The LAN transport is HTTP rather than TLS. HMAC prevents an observer from forging or replaying control requests after pairing, but it does not hide media titles, artwork, or response contents. An attacker who can actively sniff the exact one-time pairing exchange could also capture the returned device key.

For the intended home-LAN setup, pair on a WPA2/WPA3 private network, do not pair on guest/public Wi-Fi, do not create router port forwards, and keep any Windows Firewall rule limited to the Private profile. A later certificate-pinning/QR-pairing iteration would be needed for confidentiality against an already-compromised LAN.

## If a phone or key may be compromised

Right-click the MediaDeck tray shield and select **Reset phone pairing**. This invalidates the old phone key immediately. Pair the intended phone again with the new one-time code.

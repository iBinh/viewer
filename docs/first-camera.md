# Adding your first camera

Three paths, listed cheapest-to-most-manual.

## 1. WS-Discovery (LAN scan)

Library → `🔍 Discover`. The app sends a WS-Discovery probe to the
multicast group `239.255.255.250:3702`. Cameras with ONVIF responders
answer within ~5 s; the dialog lists them with their advertised model and
RTSP URI.

Pick a camera → enter credentials → the editor pre-fills name / host /
RTSP / ONVIF profile. Save.

### Deep scan and where it sweeps

WS-Discovery and mDNS are multicast, so they only ever reach the local
link. *Deep scan* adds an active sweep — a TCP knock on each host — which
is how a camera that answers neither, or sits on a subnet multicast can't
cross, gets found.

The dialog lists the subnets it can sweep, read from the machine's routing
table:

- Subnets **this machine is on** are ticked by default.
- Subnets reachable only **through a route** (another VLAN, or a VPN /
  mesh tunnel) are listed but left unticked — tick the ones you mean to
  sweep. This is the case that needs no typing: the camera's subnet shows
  up on its own, you just tick it.

The **IP range** box below the list is for anything the routing table
doesn't show — a subnet with no route of its own, or a single address. What
you type is **combined with** the ticked subnets, not used instead of them.
Accepted:

| Typed                         | Sweeps                        |
|-------------------------------|-------------------------------|
| `192.168.1.0/24`              | `192.168.1.1` … `192.168.1.254` |
| `192.168.1.10-192.168.1.200`  | that span                     |
| `192.168.1.10-200`            | the same, last octet only     |
| `192.168.1.64`                | one host                      |

Several at once, separated by commas or spaces. Typing a range also
switches the sweep on by itself. The running total is shown before you
scan; at most 4096 addresses per scan, so if the ticked subnets plus the
typed range exceed that, untick one or narrow the range.

On the [web console](web-server.md) the typed range must stay inside a
private network (`10/8`, `172.16/12`, `192.168/16`) — a server accepts a
range from any Manage user, so it isn't allowed to be aimed at the wider
internet or at its own loopback.

If discovery turns up nothing:
- Your router is blocking multicast (check WiFi AP settings — some block
  it on guest networks by default).
- The camera is on a different VLAN — tick that subnet in the Deep scan
  list (or type its range), then scan again. It has to be routable from
  here; discovery can't cross a firewall that drops the traffic.
- ONVIF is disabled in the camera config (Majestic ships with it disabled
  in some firmware revisions — flip `service.onvif.enabled = true`).

## 2. Manual

Library → `+ Add camera`. Fill in:

- **Host** — IP or hostname.
- **RTSP main** — full URI, e.g. `rtsp://192.168.1.50:554/0`. Path varies
  by firmware: OpenIPC mainline uses `/0` (main) and `/1` (sub).
- **Credentials** — kept in the platform keystore (DPAPI / Keychain /
  libsecret) or AES-GCM file fallback.

## 3. QR code

*Comes in 11c.* Future: scan a QR with `rtsp://user:pass@host:port/path`
or a JSON payload, app adds the camera in one tap.

## Common issues

- **"Failed to connect" / `stimeout` errors** — wrong RTSP path. Camera
  vendors love to pick different defaults (`/cam/realmonitor`, `/stream1`,
  `/h264`). Check the vendor docs.
- **Auth loops** — the URI takes plain user/password but credentials live
  in the keystore separately. Setting them in both places is fine; just
  the keystore copy is preferred (URI auth shows up in logs).
- **VAAPI permission denied** (Linux) — `usermod -aG render $USER` then
  re-login. The app falls back to software decode but eats more CPU.
- **Android: missing notification permission** — recording shows a
  notification while it runs. On Android 13+ the runtime permission
  prompt fires the first time you start a recording.

# Running ConnectionWatcher in a container

```bash
cd deploy/docker && docker compose up -d
```

The container needs `NET_RAW` (already in `compose.yaml`) so ping and traceroute work.

**Synology:** create the `./data` folder yourself before the first start — Synology's
Docker does not auto-create bind-mount folders and fails with "Bind mount failed".

## Bridge or host networking?

| | Bridge (default) | Host (`network_mode: host`) |
|---|---|---|
| Monitoring your targets | works | works |
| Traceroute's first hop | the Docker bridge, not your router | your real router |
| **Find my router** | returns the bridge | the real default gateway |
| **Scan local network** (Config → Discoveries) | scans the bridge subnet only | scans your real LAN |
| Port mapping | `ports:` (left side is yours to choose) | none — the app binds the host's port directly |
| Where it works | everywhere | **Linux hosts only** (Synology, QNAP, Unraid, Linux servers); Docker Desktop on Windows/macOS does not support it by default (4.34+ offers opt-in host networking) |

If you stay on bridge networking you lose nothing for monitoring. To discover your router and LAN devices
anyway, run the searches from a computer on your LAN instead: the Windows tray's **Find my router**,
**Find the network edge** and **Scan this network** menu items scan from the PC and post the results to
the container's Discoveries page (you will be asked for the admin password if one is set; tick Remember to save it, encrypted for your Windows account).

## Switching to host networking

1. In `compose.yaml`, uncomment `network_mode: host`, the `environment:`/`ASPNETCORE_URLS` lines and the
   `healthcheck:` block, and **remove** the `ports:` block. Pick a free port in `ASPNETCORE_URLS` (DSM itself
   uses 5000/5001; many NAS apps use 8080) and use the same port in the healthcheck URL. The image's built-in
   healthcheck probes port 8080, so without the override the container would report unhealthy on any other port.
2. `docker compose up -d`. The web page is now at `http://<nas-address>:<that port>/`.
3. Point the Windows tray (Tray settings) at the new URL.

**Synology Container Manager:** in the project/container network settings choose *Use the same network as
Docker host* instead of editing `ports:`.

## Notes

- With host networking the container sees every interface on the NAS. The LAN scan sweeps at most the /24
  around each private address, never public ranges.
- The container warning on search results keys on `DOTNET_RUNNING_IN_CONTAINER`, which the image always sets,
  so it appears whenever the app runs in a container. In host-network mode it is expected and you can ignore it;
  on the default bridge it is the cue to use host networking or the tray.
- The LAN scan skips interfaces without a default gateway whenever any interface has one, so virtual
  adapters (docker bridges, WSL) are not swept alongside your real network.
- Back up `./data` before pulling a new image: releases can include database migrations.

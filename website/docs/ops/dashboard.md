# Server Dashboard

The relay exposes a live HTTP dashboard on its second port. Shows everything a process-level monitor can't — what's actually happening inside the physics tick, who's in which room, what kinds of messages are flowing, the last few hundred log lines — and has an "Export snapshot" button that bundles the whole state into a JSON file for offline analysis.

## Binding

Both bind addresses are positional args:

```bash
./target/release/somnium-relay <ws-bind> <dashboard-bind>
```

Defaults: `0.0.0.0:9090` for the game socket, `0.0.0.0:9091` for the dashboard. For production, consider keeping the dashboard local-only and reaching it over SSH:

```bash
# On the server:
./target/release/somnium-relay 0.0.0.0:9090 127.0.0.1:9091

# From your workstation:
ssh -L 9091:localhost:9091 myserver
# then open http://localhost:9091/ locally
```

The dashboard has **no auth** — anyone who can reach the port can see IP addresses in the log tail, client counts, message-type histograms, etc. Don't expose it naked.

## What's on the page

| Card | Source |
|---|---|
| Uptime | `started_at.elapsed()` |
| CPU | `/proc/self/stat` vs `/proc/stat` on Linux, `sysinfo` elsewhere |
| Memory (RSS) | `/proc/self/status` VmRSS |
| Clients (now) / total / disconnected | Live count from room map + lifetime counters |
| Rooms count | `rooms.len()` |
| Inbound / Outbound | Per-request delta from previous `/metrics` fetch |
| Dropped (buffer full) | Count of `try_send` rejections — grows when a client lags |
| Physics tick p50 / p95 / p99 | Rolling 300-sample histogram of Rapier step time |
| Broadcast build p50 / p95 / p99 | Rolling histogram of "build + send" time per tick |

Below that:

- **Rooms table** — per room: client count, physics_ready, object count, claimed object count, scene-ready timestamp.
- **Clients table** — cid, address, room, uptime, rx msgs, rx bytes.
- **Messages received by type** — full breakdown by the first byte of each incoming message (MSG_PLAYER_POS, MSG_INTERACTION_UPDATE, etc.) so you can tell at a glance what kind of traffic dominates.
- **Control subtypes received** — same thing for 0xFF messages (claims, releases, player ident, persistence, etc.).
- **Log tail** — last 200 log lines, colour-coded by level, auto-scrolls when pinned to the bottom.

The dashboard polls `/metrics` every second and keeps a client-side rolling history of the last 3600 snapshots (≈ 1 hour at 1 Hz) in memory.

## /metrics endpoint

JSON snapshot of the live state. Cheap enough to poll at 1 Hz — rates are computed as deltas per-request, no rolling-window bookkeeping on the hot path.

```bash
curl http://host:9091/metrics | jq .
```

Response shape matches what the dashboard renders — see the [source schema](https://github.com/Bentlybro/SomniSync/blob/dev/server/src/metrics_server.rs) for field names.

## /export endpoint

Richer variant for offline analysis. Returns:

- Everything `/metrics` returns.
- The full 2000-line log ring (not just the live tail of 200).
- Raw physics tick + broadcast build microsecond samples (so you can reconstruct the whole distribution, not just the three percentiles).
- Server version from `CARGO_PKG_VERSION`.
- Export timestamp.

The dashboard's **Export snapshot** button hits this endpoint, pairs it with the client-side history buffer, and triggers a browser download of `somnisync-export-{ISO-timestamp}.json`. That's the file you want if you're asking someone else to diagnose a server issue — it captures both the current deep state and the recent time-series.

```bash
# Manual fetch
curl http://host:9091/export > snapshot.json
```

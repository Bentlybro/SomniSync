# SomniSync

Server-authoritative real-time physics networking for Unity. Designed for VR worlds with thousands of objects and dozens of concurrent players. Rust relay server running Rapier3D physics, binary protocol, per-client area-of-interest filtering.

Built for [Somnium Space](https://somniumspace.com) VR but works with any Unity project using `System.Net.WebSockets`.

📖 **[Full Documentation](https://bentlybro.github.io/SomniSync/)**

## Install

In Unity: **Window → Package Manager → + → Add package from git URL** and paste:

```
https://github.com/Bentlybro/SomniSync.git?path=client/Unity
```

Unity 6 (6000.0+) required.

## Quick Start

1. Import the Quick Start sample from Package Manager, then **Tools → SomniSync → Create Quick Start Scene** will scaffold a working sample for you.
2. Or wire your own scene manually:
   - Add `NetworkManager` to a GameObject
   - Set the `Server URL` (e.g. `ws://localhost:9090`) and `Room ID`
   - Add `NetworkObject` to every object you want synced
   - *(Optional)* Set `Local Player` on NetworkManager for server-side AOI filtering
3. Run **Tools → SomniSync → Validate Scene** to catch common setup mistakes before you hit Play.

Physics syncs automatically. Players can grab/push objects. First client to connect bootstraps the server's physics world.

## Architecture

```
Client A ─┐
Client B ─┤  binary wire protocol   ┌──────────────────────────────┐
Client C ─┼────────────────────────→│ Rust Relay Server            │
Client D ─┘                         │  • Rapier3D physics (50Hz)   │
                                    │  • Per-client AOI broadcast   │
                                    │  • Distance-based LOD tiers   │
                                    │  • SQLite player persistence  │
                                    └──────────────────────────────┘
                                                 │
                                    ┌────────────┴────────────────┐
                                    ↓                             ↓
                            Filtered 20Hz pos       Authoritative physics:
                            updates per client      claims, releases,
                                                    collisions, gravity.
```

- **Server runs all physics.** Clients never simulate each other's objects. Eliminates the "one laggy master drags everyone down" problem of peer-authority systems.
- **First client to connect sends SCENE_INIT** (scene description — colliders, mass, friction, initial positions). Server builds its Rapier world from that.
- **Clients only send:** grab/release, held-object positions, player location, events, variables, animator state. Everything else comes from the server.
- **Per-client AOI + LOD** — each player receives updates only for objects within 150m of them, with three distance tiers (25m every tick, 60m every 3rd, 150m every 10th). Bandwidth scales with what you can actually see.
- **Multi-room** — path-based URL routing (`ws://server/my-world`). Each room isolated, auto-destroyed when empty.
- **Deterministic shared clock** (`NetworkManager.ServerTime`) lets components like `NetworkWaypoint` do zero-bandwidth NPC pathing.

## Features

- **Drop-in components** — position, variables, RPCs, animations, particles, timers, deterministic waypoints, NPC AI
- **NetworkNPC** — one-component wandering + chase-aggro with deterministic zero-bandwidth idle paths
- **Instant-feel grabs** — client-side prediction with server reconciliation; server rejects conflicting claims and the loser unwinds cleanly
- **Per-object interaction flags** — `pushable` / `grabbable` per NetworkObject
- **Scale sync** — opt-in, adds 6 bytes per entity
- **Targeted RPCs** — `Send()` (all), `SendToAuthority()`, `SendToProxies()`
- **Quantized binary wire format** — 22 B/entity compact, 28 B with velocity (dead-reckoning) or scale
- **Deterministic IDs** — no GUID overhead, objects sorted identically on every client
- **SQLite player persistence** — typed Save/Load API keyed by Somnium account UUID (or device UUID fallback)
- **Live HTTP dashboard** — second port exposes `/metrics` + `/export` with CPU / RSS / per-client stats / physics tick percentiles / live log tail / downloadable snapshot JSON

## Components

Every synced object needs **NetworkObject**. Everything else is optional.

| Component | What it does | Docs |
|-----------|-------------|------|
| **NetworkManager** | Connection, object registration, AOI, clock sync, local player tracking | [→ Docs](docs/components/network-manager.md) |
| **NetworkObject** | Position/rotation/scale sync, grab/push interaction | [→ Docs](docs/components/network-object.md) |
| **NetworkNPC** | Deterministic wander + optional chase-aggro AI — zero idle bandwidth | [→ Docs](docs/components/network-npc.md) |
| **NetworkVariables** | Sync custom data — int, float, bool, string | [→ Docs](docs/components/network-variables.md) |
| **NetworkEvents** | RPCs — broadcast, to authority, or to proxies | [→ Docs](docs/components/network-events.md) |
| **NetworkAnimator** | Sync Animator state, parameters, triggers | [→ Docs](docs/components/network-animator.md) |
| **NetworkParticle** | Sync ParticleSystem play/stop and properties | [→ Docs](docs/components/network-particle.md) |
| **NetworkTimer** | Networked countdowns | [→ Docs](docs/components/network-timer.md) |
| **NetworkPersistence** (static) | Per-player server-backed Save/Load — float/int/bool/string, namespaced | [→ Docs](docs/components/network-persistence.md) |
| **NetworkWaypoint** | Deterministic waypoint pathing — zero bandwidth, driven by shared clock | — |
| **NetworkStats** | F3 debug overlay — FPS, bandwidth, connection status | [→ Docs](docs/components/network-stats.md) |
| **NetworkDebug** | Per-tick Unity-console dump for desync diagnosis. CSV capture is handled by `tools/bot_client.py` + `tools/netdbg_diff.py` | — |
| **PlayerPushDetector** | Auto-attached collision detection for push-to-grab | [→ Docs](docs/components/player-push-detector.md) |
| **SomniumBridgeIntegration** | One-line Somnium Space player identity hookup | — |

Not sure which to use? See [Which Components Do I Need?](docs/getting-started/which-components.md)

## Documentation

- [**Setup Guide**](docs/getting-started/setup.md) — Install, configure, connect
- [**Which Components?**](docs/getting-started/which-components.md) — Pick the right components for your objects
- [**Component Reference**](docs/README.md#components) — Detailed API for each component
- [**Protocol Spec**](docs/protocol/overview.md) — Binary format, message types, bandwidth
- [**Examples**](docs/README.md#examples) — Basic world, doors, physics playground, dungeon, vehicles, scoreboard

Or visit the full docs site: **[bentlybro.github.io/SomniSync](https://bentlybro.github.io/SomniSync/)**

## Server

Rust relay running `tokio` + `tokio-tungstenite` + `rapier3d`. Zero-copy broadcast, ArrayPool buffers, per-client filtered sends on the tokio thread pool.

```bash
cd server
cargo build --release
./target/release/somnium-relay                          # defaults: ws :9090 + dashboard :9091
./target/release/somnium-relay 0.0.0.0:9090 127.0.0.1:9091  # production shape: game public, dashboard local-only
```

Two positional args: WebSocket bind and HTTP dashboard bind. The dashboard is a separate opt-in listener — if its port is taken, the relay keeps serving game traffic.

Unauthenticated player persistence is disabled by default on the relay. For trusted local/dev relays only, set `SOMNISYNC_ALLOW_UNAUTH_PERSISTENCE=1` before starting the server; production deployments should wire persistence identity to a verifiable platform token instead of trusting client-supplied UUIDs.

## Performance

Most recent measured run on a single-vCPU DigitalOcean droplet:

| Scenario | Result |
|---|---|
| 1016 scene objects + 2 real players, ~15 min | CPU **21 %**, RSS 15 MB, 0 drops |
| Physics tick p99 | **6.4 ms** (budget 20 ms at 50 Hz) |
| Broadcast build p99 | **90 µs** |
| Per-client peak downlink | ≈ 250 KB/s |

40-user load-testing via the bot-swarm harness (`tools/bot_client.py`) is the next validation. Numbers past "a thousand objects with two players" are being tested, not claimed.

Bandwidth math:
- Per-entity wire size: 22 B (compact) / 28 B (with velocity) / 28 B (with scale)
- Broadcast rate: 20 Hz (50 ms ticks)
- AOI tiers: near (0–25 m, every tick) · mid (25–60 m, every 3rd) · far (60–150 m, every 10th) · beyond 150 m not broadcast
- Claim updates (held objects) sent at client `sendRate` (default 60 Hz) — one client per object
- Sleeping Rapier bodies skipped entirely from broadcasts — an idle world is nearly free

## Benchmarking

Three tools in the repo let you test scale without 40 real humans:

- `benchmarks/bench.py` — ping RTT, event relay latency, connection storm.
- `tools/bot_client.py` — headless bot swarm that speaks the full binary protocol; `--interact` adds claim/release/drive cycles to stress the Rapier write path.
- `tools/netdbg_diff.py` — offline CSV diff for inter-client NPC position agreement.

See [docs/ops/benchmarking.md](docs/ops/benchmarking.md) for full usage.

## License

All rights reserved. This software is proprietary and confidential. Unauthorized copying, distribution, or use of this code is strictly prohibited. Access is granted to approved collaborators only.

# SomniSync

Server-authoritative real-time physics networking for Unity. Designed for VR worlds with thousands of objects and dozens of concurrent players. Rust relay server running Rapier3D physics, binary protocol, per-client area-of-interest filtering.

Built for [Somnium Space](https://somniumspace.com) VR but works with any Unity project using `System.Net.WebSockets`.

📖 **[Full Documentation](https://bentlybro.github.io/SomniSync/)**

## Quick Start

1. Add `NetworkManager` to a GameObject in your scene
2. Set the `Server URL` (e.g. `ws://localhost:9090`)
3. Set a `Room ID` (e.g. `my-world`)
4. Add `NetworkObject` to every object you want synced
5. *(Optional)* Set `Local Player` on NetworkManager to your player transform — enables server-side AOI filtering
6. Done.

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

- **11 networking components** — position sync, variables, RPCs, animations, particles, timers, deterministic waypoints
- **Instant-feel grabs** — client-side prediction with server reconciliation; server rejects conflicting claims and the loser unwinds cleanly
- **Per-object interaction flags** — `pushable` / `grabbable` per NetworkObject
- **Scale sync** — opt-in, adds 6 bytes per entity
- **Targeted RPCs** — `Send()` (all), `SendToAuthority()`, `SendToProxies()`
- **Connection events** — `OnNetworkReady`, `OnDisconnected`
- **Quantized binary wire format** — 22 bytes/entity (f32 position + quaternion rotation), 28 bytes with velocity
- **Deterministic IDs** — no GUID overhead, objects sorted identically on every client
- **SQLite player persistence** — key/value per stable player ID (Somnium account UUID or device UUID fallback)

## Components

Every synced object needs **NetworkObject**. Everything else is optional.

| Component | What it does | Docs |
|-----------|-------------|------|
| **NetworkManager** | Connection, object registration, AOI, local player tracking | [→ Docs](docs/components/network-manager.md) |
| **NetworkObject** | Position/rotation/scale sync, grab/push interaction | [→ Docs](docs/components/network-object.md) |
| **NetworkVariables** | Sync custom data — int, float, bool, string | [→ Docs](docs/components/network-variables.md) |
| **NetworkEvents** | RPCs — broadcast, to authority, or to proxies | [→ Docs](docs/components/network-events.md) |
| **NetworkAnimator** | Sync Animator state, parameters, triggers | [→ Docs](docs/components/network-animator.md) |
| **NetworkParticle** | Sync ParticleSystem play/stop and properties | [→ Docs](docs/components/network-particle.md) |
| **NetworkTimer** | Networked countdowns | [→ Docs](docs/components/network-timer.md) |
| **NetworkWaypoint** | Deterministic NPC pathing — zero bandwidth, driven by shared clock | — |
| **NetworkStats** | F3 debug overlay — FPS, bandwidth, connection status | [→ Docs](docs/components/network-stats.md) |
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
./target/release/somnium-relay              # default: ws://0.0.0.0:9090
./target/release/somnium-relay 0.0.0.0:8080 # custom port
```

Minimal resource usage — a single-vCPU VPS handles hundreds of connections and thousands of physics bodies.

## Performance

Recent tests on a 2Gbps droplet:

| Objects | Concurrent clients | Status |
|---------|-------------------|--------|
| 1000 physics spheres | 1 | Smooth, all falling at once |
| *your numbers here* | *here* | *here* |

Typical per-client downlink bandwidth at 1000 dynamic objects × 20Hz broadcast: ≤ 560 KB/s (before AOI trims it further). Sleeping bodies are skipped entirely — a quiet room is nearly free.

Bandwidth math:
- Per-entity wire size: 22 B (compact) / 28 B (with velocity) / 28 B (with scale)
- Broadcast rate: 20 Hz (50 ms)
- AOI tiers: near (0–25m, every tick) · mid (25–60m, every 3rd) · far (60–150m, every 10th) · beyond 150m not broadcast
- Claim updates (held objects) sent at client `sendRate` (default 60Hz) — one client per object

## License

All rights reserved. This software is proprietary and confidential. Unauthorized copying, distribution, or use of this code is strictly prohibited. Access is granted to approved collaborators only.

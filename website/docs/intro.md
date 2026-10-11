---
slug: /
sidebar_position: 0
---

# SomniSync

Server-authoritative real-time physics networking for Unity and Somnium Space worlds. A Rust relay running Rapier3D owns the physics simulation; clients send interactions and receive compact object updates.

Built for [Somnium Space](https://somniumspace.com) VR but works with any Unity project using `System.Net.WebSockets`.

## Quick Start

1. Install the Unity package and open **Tools → SomniSync → Setup Wizard**.
2. For a Somnium upload, prepare creator-specific scripts in `Assets/#User/<creator>.somnisync` before adding components.
3. Set a relay URL and room, add a **NetworkManager**, and add **NetworkObject** to synced colliders.
4. Run **Validate scene**, then test with two clients and a running relay.

The first client to connect sends the scene description; the server builds its physics world from that and broadcasts authoritative state to everyone after.

→ [Full setup guide](/docs/getting-started/setup)

## Architecture, at a glance

```
Client A ─┐
Client B ─┤   binary wire protocol   ┌──────────────────────────────────┐
Client C ─┼─────────────────────────→│ Rust Relay Server                │
Client D ─┘                          │  • Rapier3D physics (50/30 Hz)      │
                                     │  • Per-client AOI broadcast      │
                                     │  • Distance-based LOD tiers      │
                                     │  • SQLite player persistence     │
                                     │  • Live /metrics HTTP dashboard  │
                                     └──────────────────────────────────┘
```

- **Server owns physics.** Clients never simulate each other's objects. No "laggy master drags everyone down" problem.
- **Clients only send** inputs: claim/release, held-object positions, their own player position (for AOI), events, variables, animator state.
- **Per-client AOI + LOD** — with a known player position, each player receives moving objects within 500 m in three distance tiers (50 m, 150 m, 500 m). Without a player position, the relay sends all moving objects.
- **Shared scene clock** (`NetworkManager.ServerTime` + server-broadcast `SceneReadyServerMs`) lets deterministic subsystems like `NetworkNPC` wander with zero bandwidth.

## Current state

| Scenario | Status |
|---|---|
| 4,000 moving spheres, 4 clients on an isolated 2-vCPU VPS | ~19.5 frames/s and 283 KB/s per client with AOI; no drops |
| 4,000 moving spheres, 40 synthetic clients on the same VPS | 19.7 frames/s and 288 KB/s per client; physics p95 13.0 ms; no drops |
| 8,000 moving spheres, 2 clients on the same VPS | ~20 frames/s and 480 KB/s per client; physics adapted to 30 Hz |
| Unity Editor with 4,000 networked spheres and 3 bot clients | All clients joined the VPS room; 489 held-object updates, 0 dropped sends |
| Runtime spawn/despawn of prefabs | Working |
| Persistent player data (SQLite) | Disabled by default until player identity is verified; trusted local testing only |
| Deterministic wander NPCs | Working |
| Chase-aggro AI NPCs (server-authoritative re-anchor) | Working |

These numbers come from generated simple spheres and do not guarantee capacity for a complex Somnium world. See the [repository README](https://github.com/Bentlybro/SomniSync#measured-performance) for test conditions and open limits.

## Components

Every synced object needs **NetworkObject**. Everything else is layered on top.

| I want to… | Add |
|---|---|
| Sync position/rotation | `NetworkObject` (required) |
| Sync scale too | `NetworkObject` with `syncScale` enabled |
| Sync custom variables | + `NetworkVariables` |
| Fire RPC-style events | + `NetworkEvents` |
| Sync Animator state | + `NetworkAnimator` |
| Sync particle effects | + `NetworkParticle` |
| Networked countdown timer | + `NetworkTimer` |
| Wandering / chasing NPC | + `NetworkNPC` |
| Player-scoped save/load (SQLite) | `NetworkPersistence.Save / Load` (static API) |
| F3 debug overlay | + `NetworkStats` |
| Per-tick Unity-console diagnostics | + `NetworkDebug` |

→ [Which components do I need?](/docs/getting-started/which-components)

## Ops

Run the server with two positional args — WebSocket bind address + HTTP dashboard bind address:

```bash
./target/release/somnium-relay 0.0.0.0:9090 127.0.0.1:9091
```

The dashboard at `http://host:9091/` shows CPU / RSS / client list / message-type histogram / physics tick percentiles / live log tail, and has an "Export snapshot" button that dumps everything (including up to an hour of 1 Hz history) as a JSON file for offline analysis.

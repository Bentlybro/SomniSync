---
slug: /
sidebar_position: 0
---

# SomniSync

Server-authoritative real-time physics networking for Unity. Designed for VR worlds with hundreds of physics objects and dozens of concurrent players. A Rust relay server running Rapier3D owns the physics simulation; clients only send inputs.

Built for [Somnium Space](https://somniumspace.com) VR but works with any Unity project using `System.Net.WebSockets`.

## Quick Start

1. Add a **NetworkManager** component to a GameObject in your scene.
2. Set `Server URL` (e.g. `ws://localhost:9090`) and `Room ID`.
3. Add a **NetworkObject** component to every GameObject you want synced.
4. *(Optional)* Drop **SomniumBridgeIntegration** on the scene to hook Somnium account identity into persistence + AOI.

The first client to connect sends the scene description; the server builds its physics world from that and broadcasts authoritative state to everyone after.

→ [Full setup guide](/docs/getting-started/setup)

## Architecture, at a glance

```
Client A ─┐
Client B ─┤   binary wire protocol   ┌──────────────────────────────────┐
Client C ─┼─────────────────────────→│ Rust Relay Server                │
Client D ─┘                          │  • Rapier3D physics (50 Hz)      │
                                     │  • Per-client AOI broadcast      │
                                     │  • Distance-based LOD tiers      │
                                     │  • SQLite player persistence     │
                                     │  • Live /metrics HTTP dashboard  │
                                     └──────────────────────────────────┘
```

- **Server owns physics.** Clients never simulate each other's objects. No "laggy master drags everyone down" problem.
- **Clients only send** inputs: claim/release, held-object positions, their own player position (for AOI), events, variables, animator state.
- **Per-client AOI + LOD** — each player receives updates only for objects within ~150 m, in three distance tiers that broadcast at different rates.
- **Shared scene clock** (`NetworkManager.ServerTime` + server-broadcast `SceneReadyServerMs`) lets deterministic subsystems like `NetworkNPC` wander with zero bandwidth.

## Current state

| Scenario | Status |
|---|---|
| 1000+ static physics objects at rest | Tested, clean |
| 1000+ objects with 2 concurrent players grabbing/pushing | Tested, physics tick p99 < 10 ms |
| 40 concurrent bot clients | Harness exists (`tools/bot_client.py`) — scaling test ongoing |
| Runtime spawn/despawn of prefabs | Working |
| Persistent player data (SQLite) | Working |
| Deterministic wander NPCs | Working |
| Chase-aggro AI NPCs (server-authoritative re-anchor) | Working |

Hard numbers past "a few thousand objects" are not claimed yet — the harness to validate them (a headless bot swarm + live metrics dashboard) now exists and is what benchmarks are run against. When we hit a real ceiling we'll say so.

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
| Per-tick diagnostic CSV dump | + `NetworkDebug` |

→ [Which components do I need?](/docs/getting-started/which-components)

## Ops

Run the server with two positional args — WebSocket bind address + HTTP dashboard bind address:

```bash
./target/release/somnium-relay 0.0.0.0:9090 127.0.0.1:9091
```

The dashboard at `http://host:9091/` shows CPU / RSS / client list / message-type histogram / physics tick percentiles / live log tail, and has an "Export snapshot" button that dumps everything (including up to an hour of 1 Hz history) as a JSON file for offline analysis.

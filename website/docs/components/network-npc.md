# NetworkNPC

One component, one NPC. Wandering + optional chase-aggro against the local player. Designed to scale to many NPCs without each one adding bandwidth — the wander path is deterministic from a shared server clock.

Attach to any GameObject that should behave like an NPC. NetworkNPC requires a sibling **NetworkObject** and takes care of:

- Telling the server to keep the body kinematic (so Rapier doesn't fight the AI with gravity).
- Computing the wander path from `NetworkManager.ServerTime` and a per-NPC seed — **every client computes the same position for the same server time**, so idle NPCs cost zero bytes.
- On chase, using the existing claim/release mechanism to pick one client-authority per NPC (the one chasing); losing bidders back off with a cooldown.
- Re-anchoring the wander path to the server's authoritative release pose when a chase ends, so NPCs don't drift apart between clients after being chased.

## Inspector

| Property | Default | Description |
|---|---|---|
| `canWander` | `true` | Wander within a radius while idle. Turn off for stationary sentinels. |
| `wanderRadius` | `8` m | How far from its spawn point the NPC will drift. |
| `wanderSpeed` | `2` m/s | Movement speed while wandering. |
| `pauseMin` / `pauseMax` | `0.5` / `2.5` s | Per-waypoint pause duration picked deterministically from the seed. |
| `canChase` | `false` | Chase the local player when they come near. |
| `aggroRadius` | `10` m | Become aggro'd when the local player is within this distance. |
| `deaggroRadius` | `14` m | Release when the player is farther than this. Hysteresis — must be > aggroRadius. |
| `chaseSpeed` | `4` m/s | Movement speed while chasing. |
| `minContactDistance` | `0.8` m | Stop this close to the player so the NPC doesn't climb into them. |
| `reclaimCooldown` | `1.5` s | Wait this long after a rejected claim before trying again. |
| `faceDirection` | `true` | Rotate to face the direction of motion. |
| `turnSpeed` | `540` °/s | Max rotation speed. 0 = instant. |
| `seedOverride` | `-1` | Random seed. -1 means derive from `NetworkObject.LocalId` — ensures uniqueness per-NPC, determinism across clients. |

## Usage

```
GameObject "WanderCube"
├── Rigidbody (any config)
├── NetworkObject
└── NetworkNPC                     canWander=true, wanderRadius=8
```

That's it. No code.

For a chasing monster:

```
GameObject "AngryCube"
├── Rigidbody
├── NetworkObject
└── NetworkNPC                     canWander=true, canChase=true,
                                   aggroRadius=10, chaseSpeed=4
```

Every client in the room sees the NPC wander along identical paths. When a player gets close, the first client to notice grabs the NPC via `NetworkObject.Grab()`, drives it kinematically toward the local player, and releases when the player escapes (`deaggroRadius`). The server broadcasts the authoritative release pose + timestamp, and everyone's wander path re-anchors to that point — no post-chase drift.

## How the deterministic wander stays in sync

Every NPC has a **wander epoch** — the server time from which its wander path is computed. For scene-baked NPCs the epoch is `NetworkManager.SceneReadyServerMs` (sent by the server in `CTRL_WELCOME` / `CTRL_PHYSICS_READY`). For runtime-spawned NPCs the epoch comes from `NetworkObject.SpawnServerMs` on the `CTRL_SPAWN` message.

Because every client reads the same authoritative epoch, `pos(t, seed)` returns the same vector on every screen. Idle NPCs broadcast nothing — the server sees them sleeping in Rapier, the clients compute positions locally from the shared clock.

Chase is the only non-free part. During a chase, the authority-client sends standard held-object updates at its send rate (default 60 Hz). One NPC being chased costs the same bandwidth as one player grabbing any object.

## Diagnostic hooks

`NetworkNPC` exposes read-only fields consumed by `NetworkDebug` for desync investigation:

- `DebugOrigin` — current wander anchor position.
- `DebugWanderEpoch` — current wander epoch (server unix seconds).
- `DebugAnchoredReleaseMs` — `LastReleaseServerMs` value most recently re-anchored against.

See [NetworkDebug](./) for CSV logging + `tools/netdbg_diff.py` for offline position-delta analysis between two clients.

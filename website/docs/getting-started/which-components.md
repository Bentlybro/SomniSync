# Which Components Do I Need?

Every synced object needs **NetworkObject** — it's the foundation that handles position and rotation sync. Everything else is optional, added based on what your object needs.

## Quick Reference

| I want to... | Component | Required? |
|--------------|-----------|-----------|
| Sync position and rotation | `NetworkObject` | **Yes — always** |
| Sync custom data (health, scores, open/closed states) | `NetworkVariables` | Optional |
| Send events/RPCs (explode, damage, trigger something) | `NetworkEvents` | Optional |
| Sync Unity Animator state and parameters | `NetworkAnimator` | Optional |
| Sync ParticleSystem play/stop and properties | `NetworkParticle` | Optional |
| Add networked countdown timers | `NetworkTimer` | Optional |

## Common Setups

### Static prop that syncs position (e.g. a crate)
- `NetworkObject`

### Throwable ball
- `NetworkObject` — server-authoritative position/rotation updates include velocity hints from Rapier

### Interactive door
- `NetworkObject` — syncs position
- `NetworkVariables` — syncs `isOpen` state
- `NetworkAnimator` — syncs open/close animation

### Exploding barrel
- `NetworkObject` — syncs position
- `NetworkEvents` — sends "explode" event to all clients
- `NetworkParticle` — syncs explosion VFX

### NPC with animations
- `NetworkObject` — syncs position
- `NetworkAnimator` — syncs walk/attack/idle states
- `NetworkVariables` — syncs health, state data

### Timed bomb
- `NetworkObject` — syncs position
- `NetworkTimer` — networked countdown
- `NetworkEvents` — triggers explosion when timer finishes

### Physics puzzle piece
- `NetworkObject` — syncs server-authoritative physics state

## Fast-Moving Physics Objects

Older SomniSync docs referenced a separate `NetworkRigidbody` component. The current server-authoritative architecture does not need that component: the relay simulates dynamic bodies in Rapier and broadcasts position, rotation, and velocity in `MSG_POSITION_VELOCITY`.

### NetworkObject (required on everything)
Attach `NetworkObject` to every synced object with a supported root collider. Dynamic Rigidbody objects are simulated on the server; clients interpolate toward the authoritative snapshots.

### Rule of Thumb

| Object type | Component setup |
|------------|----------------------|
| Thrown/launched objects | `NetworkObject` + dynamic `Rigidbody` |
| Explosions / debris | `NetworkObject` + dynamic `Rigidbody` |
| Slow-moving doors/platforms | `NetworkObject` plus `NetworkVariables`/`NetworkAnimator` if needed |
| Objects that mostly sit still | `NetworkObject` |
| Small props on a table | `NetworkObject` |

**When in doubt:** start with `NetworkObject`. Add optional components only for custom state, events, animation, particles, timers, or NPC behavior.

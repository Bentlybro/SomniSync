# Which Components Do I Need?

Every synced object needs **NetworkObject** — it's the foundation that handles position and rotation sync. Everything else is optional, added based on what your object needs.

## Quick Reference

| I want to... | Component | Required? |
|--------------|-----------|-----------|
| Sync position and rotation | `NetworkObject` | **Yes — always** |
| Smooth fast-moving objects (thrown, explosions, vehicles) | `NetworkRigidbody` | Optional |
| Sync custom data (health, scores, open/closed states) | `NetworkVariables` | Optional |
| Send events/RPCs (explode, damage, trigger something) | `NetworkEvents` | Optional |
| Sync Unity Animator state and parameters | `NetworkAnimator` | Optional |
| Sync ParticleSystem play/stop and properties | `NetworkParticle` | Optional |
| Add networked countdown timers | `NetworkTimer` | Optional |
| Spawn/despawn objects at runtime across all clients | `NetworkManager.RegisterObject` / `UnregisterObject` | Optional |

## Common Setups

### Static prop that syncs position (e.g. a crate)
- `NetworkObject`

### Throwable ball
- `NetworkObject` — syncs where it is
- `NetworkRigidbody` — syncs velocity for smooth flight

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
- `NetworkObject` — syncs position
- `NetworkRigidbody` — syncs velocity for smooth physics

### Runtime-spawned object (e.g. a dropped item, spawned enemy)
- `NetworkObject` — syncs position
- `NetworkManager.RegisterObject(netObj)` — tells all clients to instantiate it
- `NetworkVariables` *(optional)* — syncs state like health or item type

## NetworkObject vs NetworkRigidbody — When Do I Need Both?

This is the most common question, so here's a clear explanation.

### NetworkObject (required on everything)
Sends **where the object is** — position and rotation. Every tick, it packs the object's position into 14 bytes and sends it. The receiver interpolates to that position.

### NetworkRigidbody (optional — add for fast-moving physics objects)
Sends **how fast the object is moving** — velocity and angular velocity (15 bytes). The receiver applies that velocity so the object moves naturally between position updates.

### Why does this matter?

**Without NetworkRigidbody** (position-only sync):
```
Tick 1: Ball is at (0, 5, 0)     → receiver teleports to (0, 5, 0)
Tick 2: Ball is at (0, 4.8, 2)   → receiver teleports to (0, 4.8, 2)
Tick 3: Ball is at (0, 4.5, 4)   → receiver teleports to (0, 4.5, 4)
```
Between ticks the ball sits still, then jumps. At 60 updates/sec this is usually fine, but on fast objects you can see micro-stuttering.

**With NetworkRigidbody** (position + velocity sync):
```
Tick 1: Ball is at (0, 5, 0), velocity = (0, -2, 10)
  → receiver applies velocity, ball moves smoothly between ticks
Tick 2: Position update corrects any drift
```
The ball flies through the air naturally because the receiver knows its speed and direction.

### Rule of Thumb

| Object type | Need NetworkRigidbody? |
|------------|----------------------|
| Thrown/launched objects | **Yes** |
| Explosions / debris | **Yes** |
| Vehicles | **Yes** |
| Slow-moving doors/platforms | No |
| Objects that mostly sit still | No |
| Small props on a table | No |

**When in doubt:** Add it. It's only 15 bytes per update and only sends when velocity changes.

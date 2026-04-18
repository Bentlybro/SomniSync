# NetworkRigidbody

Syncs Rigidbody velocity and angular velocity across clients using half-precision floats. **15 bytes per update.**

**Use alongside `NetworkObject`.** NetworkObject syncs *where the object is*. NetworkRigidbody syncs *how fast it's moving* — so receivers can extrapolate smoothly between position updates.

See [Which Components Do I Need?](../getting-started/which-components.md) for when to use this.

## Inspector Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `syncRate` | float | `10` | How often to check for velocity changes (Hz) |
| `velocityThreshold` | float | `0.05` | Minimum velocity change to trigger sync (m/s) |
| `angularVelocityThreshold` | float | `0.1` | Minimum angular velocity change (rad/s) |
| `applyReceivedVelocity` | bool | `true` | Apply received velocity for extrapolation |

## How It Works

1. Authority (master or claimed owner) monitors velocity each `FixedUpdate`
2. If velocity changed beyond threshold → sends 15-byte update
3. When object stops → sends zero velocity (so receivers stop too)
4. Receivers apply velocity to their local Rigidbody for smooth extrapolation

**No code needed** — just add the component and it works automatically.

## Protocol

```
[0x08][u16 obj_id][half vx][half vy][half vz][half avx][half avy][half avz]
= 15 bytes
```

All values use half-precision floats (16-bit).

## When to Use

| Scenario | Need NetworkRigidbody? |
|----------|----------------------|
| Ball thrown across the room | **Yes** — smooth flight arc |
| Explosion debris flying everywhere | **Yes** — natural trajectories |
| Vehicle moving at speed | **Yes** — smooth driving |
| Door slowly opening | No — position sync is enough |
| Crate sitting on a shelf | No — not moving |
| Small props on a table | No — movement is minimal |

## Example: Throwable Object

No code needed — just add components:

```
GameObject "Ball"
├── Rigidbody
├── NetworkObject (entityType: "ball")
└── NetworkRigidbody
```

When a player grabs and throws:
1. `NetworkObject.Grab()` claims ownership
2. Player applies force (standard Unity physics)
3. `NetworkRigidbody` detects velocity change → sends to all clients
4. Other clients see the ball flying with correct velocity + trajectory
5. Position updates from `NetworkObject` correct any drift
6. Ball settles → auto-releases back to master

## ⚠️ Warning: Don't Use on Bulk Physics Objects

NetworkRigidbody sends 15 bytes per object per sync tick. On a few objects this is nothing. On hundreds or thousands it adds up fast:

- 50 objects at 10 Hz = 500 updates/sec ✅
- 1000 objects at 30 Hz = 30,000 updates/sec ❌ (bulk objects should not be using this)

**Rule:** Only add NetworkRigidbody to objects that **individually matter** — a thrown ball, a vehicle, a wrecking ball. For bulk physics objects (stacked cubes, debris piles), `NetworkObject` alone is enough. The server's authoritative 0x0B broadcast already carries velocity per entity for dead-reckoning extrapolation on every client; NetworkRigidbody is only for cases where you need to write velocity *from* the client (thrower) rather than read it from the server.

## Tips

- Lower `syncRate` if bandwidth is tight (5Hz is often enough)
- Increase `velocityThreshold` to skip tiny velocity jitters
- Set `applyReceivedVelocity = false` if you handle extrapolation yourself
- Only sends when velocity actually changes — zero cost for stationary objects

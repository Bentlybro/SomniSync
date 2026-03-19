# Area of Interest (AOI)

Server-side spatial filtering. Clients only receive updates for objects within range, with distance-based tick rate reduction.

## How It Works

1. Each client sends its player position to the server at 10Hz (`0x05` message, 13 bytes)
2. Server tracks all player positions and all object positions
3. When forwarding position updates (`0x01`, `0x03`), server filters per-client:

| Distance | Update Rate | Behavior |
|----------|-------------|----------|
| **< 50m** (near) | Every tick | Full rate, buttery smooth |
| **50-150m** (mid) | Every 3rd tick | Slightly reduced, still good |
| **150-300m** (far) | Every 10th tick | Minimal updates, saves bandwidth |
| **> 300m** | Culled | No updates sent at all |

## What Gets Filtered

- `0x01` Position updates (master → spectators): **AOI filtered**
- `0x03` Interaction updates (any → others): **AOI filtered**
- `0x02` Snapshots: **NOT filtered** (late joiners need full state)
- `0x04` Variable updates: **NOT filtered** (small messages, game logic may need them regardless of distance)
- `0xFF` Control messages: **NOT filtered** (claims, releases, role changes)

## Protocol

### Player Position Report (0x05)

Sent by every client at 10Hz:
```
[0x05][f32 x][f32 y][f32 z]
```
13 bytes total. Server stores this per-client for AOI calculations.

## Bandwidth Savings

Example: 5000 objects, player near 500 of them:

| Without AOI | With AOI |
|-------------|----------|
| 5000 × 26B = 130KB/tick | 500 × 26B = 13KB/tick |
| ~3.9 MB/s at 30Hz | ~390 KB/s at 30Hz |

**~10x bandwidth reduction** in typical scenarios.

## Server Configuration

AOI distances are currently compile-time constants in `main.rs`:
```rust
const AOI_NEAR: f32 = 50.0;
const AOI_MID: f32 = 150.0;
const AOI_FAR: f32 = 300.0;
```

## Notes

- Object positions are updated server-side from every `0x01`, `0x02`, and `0x03` message
- The server is still a relay — it doesn't simulate anything, just reads positions from messages it forwards
- AOI uses squared distance for performance (no sqrt)
- Tick counter is per-client for fair rate limiting

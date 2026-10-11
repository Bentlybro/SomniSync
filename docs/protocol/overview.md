# Protocol Overview

All messages are **binary**. No JSON on hot paths. Little-endian throughout.

Connect to `ws://relay/<room>?scene=<16-hex-digit fingerprint>`. The Unity manager calculates the fingerprint from scene path/version, ordered `NetworkObject` layout, transforms and collider physics, supported static colliders, and runtime prefab list. The relay checks it before registering the client; a mismatch gets `CTRL_ERROR` code 2 and the socket closes. Missing or malformed fingerprints fail the WebSocket handshake. This is a compatibility check, not an authentication token.

Positions go over the wire as `f32 × 3`; rotations as four `i16` values (signed, ×32767-quantised unit quaternion); velocities and scales as `f16` (IEEE 754 half-precision). The exact payload layout depends on the message type below.

## Message types

First byte of every message is one of:

| Byte | Name | Direction | Purpose |
|------|------|-----------|---------|
| `0x03` | `MSG_INTERACTION_UPDATE` | client → server → others | Held-object position/rotation (22 B per entity) |
| `0x04` | `MSG_VARIABLE_UPDATE` | writer/holder → server → others | `NetworkVariables` change; type bit 7 marks late-join replay |
| `0x06` | `MSG_EVENT` | any → server → others | Fire-and-forget RPC (`NetworkEvents`) |
| `0x07` | `MSG_ANIMATOR` | any → server → others | Animator state/params (`NetworkAnimator`) |
| `0x09` | `MSG_SCALED_INTERACTION` | client → server → others | Held-object pose + scale (28 B per entity) |
| `0x0B` | `MSG_POSITION_VELOCITY` | **server → clients** | Authoritative snapshot with dead-reckoning velocity (28 B per entity) |
| `0x0C` | `MSG_PLAYER_POS` | client → server | Player position + capsule dims for server-side AOI |
| `0x0D` | `MSG_PUSH_IMPULSE` | client → server | One-shot impulse on an object |
| `0x0E` | `MSG_TIME_PING` | client → server | Clock sync ping (server replies with `CTRL_TIME_PONG`) |
| `0x0F` | `MSG_POSITION_COMPACT` | **server → capable clients** | Authoritative position/rotation without velocity (22 B per entity) |
| `0x10` | `MSG_SCENE_INIT` | **first client → server** | Bootstrap the physics world (colliders, mass, friction, ...) |
| `0x12` | `MSG_VARIABLE_SNAPSHOT` | **server → joining client** | Cached persistent variables in chunks of at most 64 KiB |
| `0xFF` | `MSG_CONTROL` | either | See control subtypes below |

`MSG_POSITION_VELOCITY` (0x0B) and `MSG_POSITION_COMPACT` (0x0F) are server-authored position broadcasts. Client-authored 0x0B/0x0F is ignored.

## Entity layouts

### Compact entity — 22 bytes

Used in `MSG_INTERACTION_UPDATE`.

```
[u16 local_id]
[f32 px][f32 py][f32 pz]             // 3 × 4 = 12 B
[i16 qx][i16 qy][i16 qz][i16 qw]     // 4 × 2 = 8 B   (÷32767 to decode)
```

### Velocity entity — 28 bytes

Used in `MSG_POSITION_VELOCITY`. Compact + 3 × `f16` for linear velocity.

```
[22 B compact entity]
[f16 vx][f16 vy][f16 vz]             // 6 B
```

### Scaled entity — 28 bytes

Used in `MSG_SCALED_INTERACTION`. Compact + 3 × `f16` for scale.

## Message framing

### `MSG_POSITION_VELOCITY` (0x0B) — server → clients

```
[u8 0x0B]
[u16 sender_client_id]         // 0 = server
[u16 sequence]                 // monotonic, per-sender
[u16 count]
[count × 28-byte velocity entity]
```

### `MSG_POSITION_COMPACT` (0x0F) ? server ? capable clients

The same seven-byte header as 0x0B, followed by `count ? 22-byte compact entity`. The client advertises support with `CTRL_CLIENT_CAPS` flag bit 0.

### `MSG_INTERACTION_UPDATE` (0x03)

```
[u8 0x03]
[u16 count]
[count × 22-byte compact entity]
```

### Variable state (0x04 and 0x12)

Live updates use `[0x04][u16 object_id][u8 count]` followed by `count` records: `[u8 name_len][UTF-8 name][u8 type_flags][u16 value_len][value]`. The low two type bits select int, float, bool, or string; bit 7 asks the relay to keep the latest value for a late joiner. The server elects one writer for unclaimed state. On join it sends cached records as `[0x12][u16 count]` followed by `[u16 object_id][record]` for each value. The cache is per room, in memory, with a 2 MiB and 16,384-value cap.

### `MSG_PLAYER_POS` (0x0C) — 21 bytes

```
[u8 0x0C]
[f32 x][f32 y][f32 z]
[f32 radius][f32 half_height]
```

### `MSG_PUSH_IMPULSE` (0x0D) — 15 bytes

```
[u8 0x0D]
[u16 obj_id]
[f32 vx][f32 vy][f32 vz]
```

### `MSG_TIME_PING` (0x0E) — 9 bytes

```
[u8 0x0E]
[u64 client_local_ms]
```

Server replies with `CTRL_TIME_PONG` echoing the client timestamp + the server's own unix ms.

### `MSG_SCENE_INIT` (0x10)

```
[u8 0x10]
[u16 count]
[count × 60-byte scene object]
```

Each 60-byte scene object:

```
[u16 id]
[u8  body_type]    // 0 = dynamic, 1 = fixed, 2 = kinematic-position
[u8  shape_type]   // 0 = ball, 1 = cuboid, 2 = capsule
[f32 px, py, pz]
[f32 rot_euler_x, y, z]     // degrees, XYZ order
[f32 shape_params_0, 1, 2]  // ball: [r,_,_] | cuboid: [hx,hy,hz] | capsule: [r,hh,_]
[f32 mass]
[f32 restitution][f32 friction]
[f32 linear_damping][f32 angular_damping]
```

## Control subtypes (inside 0xFF)

```
[u8 0xFF][u8 subtype][payload...]
```

| Subtype | Name | Direction | Payload |
|---------|------|-----------|---------|
| `0x01` | `CTRL_WELCOME` | server → client | `[u16 client_id][u8 needs_scene_init][u64 unix_ms][u64 scene_ready_ms][u8 persistence_available]` |
| `0x03` | `CTRL_STATE_WRITER` | server → clients | `[u16 client_id]` elected writer for unclaimed component state |
| `0x05` | `CTRL_CLAIM` | any → server → others | `[u16 obj_id][u16 sender_cid]` |
| `0x06` | `CTRL_RELEASE` | any → server → others | `[u16 obj_id][u16 sender_cid][f32 px,py,pz][i16 qx,qy,qz,qw][u64 server_ms]` |
| `0x07` | `CTRL_CLAIM_REJECTED` | server → requester | `[u16 obj_id]` |
| `0x08` | `CTRL_PHYSICS_READY` | server → all | `[u64 scene_ready_ms]` |
| `0x0B` | `CTRL_SPAWN_REQUEST` | client → server | `[u8 prefab_idx][60 B scene_object]` (id=0) |
| `0x0C` | `CTRL_SPAWN` | server → all | `[u16 id][u8 prefab_idx][60 B scene_object][u64 spawn_server_ms]` |
| `0x0D` | `CTRL_DESPAWN` | either | `[u16 id]` |
| `0x0E` | `CTRL_TIME_PONG` | server → client | `[u64 client_local_ms_echoed][u64 server_ms]` |
| `0x20` | `CTRL_PLAYER_IDENT` | client → server | `[u8 len][utf8 player_uuid]` |
| `0x21` | `CTRL_PERSIST_SET` | client → server | `[u8 ns_len][ns][u8 key_len][key][u16 val_len][value]` |
| `0x22` | `CTRL_PERSIST_GET` | client → server | `[u16 req_id][u8 ns_len][ns][u8 key_len][key]` |
| `0x23` | `CTRL_PERSIST_DATA` | server → client | `[u16 req_id][u8 found][u16 val_len][value]` |
| `0xFE` | `CTRL_ERROR` | server → client | Application-level error notification |

### About `scene_ready_ms` and `spawn_server_ms`

Every `NetworkObject` needs a shared birth timestamp so deterministic subsystems (e.g. `NetworkNPC`'s wander sampler) anchor to the same point in time on every client — otherwise two clients joining at different wall-clock moments compute positions from different slices of the same deterministic path.

- `scene_ready_ms` is stamped by the server when the first `SCENE_INIT` lands and sent in both `CTRL_WELCOME` and `CTRL_PHYSICS_READY`. Scene-baked objects anchor to this.
- `spawn_server_ms` rides on `CTRL_SPAWN` so runtime-spawned prefabs get their own per-object birth timestamp.

## Bandwidth, honestly

Per-entity sizes are fixed. What you actually ship per second depends on how many objects are **awake** (Rapier sleeps stationary bodies, skipping them in the broadcast) and how AOI trims the per-client view:

- Server broadcast rate: 20 Hz (50 ms ticks)
- Per-client AOI tiers: `near` 0–25 m every tick · `mid` 25–60 m every 3rd · `far` 60–150 m every 10th · > 150 m not sent
- Held-object updates (`MSG_INTERACTION_UPDATE`) send at client `sendRate` (default 60 Hz), one sender per held object

A single player in a world of 1000 static physics objects receives effectively zero ongoing broadcast traffic (all bodies sleeping). Waking them up with interaction is what drives the bill.

Measured 4,000- and 8,000-object scenarios, including their hardware and limits, are in the [repository README](https://github.com/Bentlybro/SomniSync#measured-performance). Physics normally steps at 50 Hz and can adapt to 30 Hz under sustained load. The one-time `MSG_SCENE_INIT` accepts at most 16,384 objects; other inbound messages are limited to 256 KiB. Missing player positions disable AOI filtering for that client.

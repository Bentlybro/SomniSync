# Protocol Overview

All messages are **binary**. No JSON on hot paths. Half-precision floats for positions, quantized u16 for rotation.

## Message Types

| Byte | Constant | Direction | Purpose |
|------|----------|-----------|---------|
| `0x01` | `MSG_POSITION` | master → server → spectators | Batch position/rotation for moving objects (14B/entity) |
| `0x02` | `MSG_SNAPSHOT` | master → server → spectators | Full state of all objects (late-joiner sync) |
| `0x03` | `MSG_INTERACTION_UPDATE` | any → server → others | Position/rotation for claimed objects |
| `0x04` | `MSG_VARIABLE_UPDATE` | any → server → others | Custom variable changes (NetworkVariables) |
| `0x05` | `MSG_SCALED_POSITION` | master → server → spectators | Position/rotation/scale for syncScale objects (20B/entity) |
| `0x06` | `MSG_EVENT` | any → server → others | Fire-and-forget events (NetworkEvents) |
| `0x07` | `MSG_ANIMATOR` | any → server → others | Animation state/params (NetworkAnimator) |
| `0x08` | `MSG_RIGIDBODY` | any → server → others | Velocity + angular velocity (NetworkRigidbody) |
| `0x09` | `MSG_SCALED_INTERACTION` | any → server → others | Position/rotation/scale for claimed syncScale objects |
| `0xFF` | `MSG_CONTROL` | server ↔ client | Role assignment, ownership, spawn/despawn commands |

## Position Format (0x01, 0x02, 0x03)

```
[u8 type][u16 count][per entity: u16 id, half x, half y, half z, u16 rx, u16 ry, u16 rz]
```

Per entity: **14 bytes** (2 id + 6 half-float pos + 6 u16 rot)

- Position: half-precision float (16-bit IEEE 754) — range ±65504, ~0.001m precision
- Rotation: 0–360° quantized to u16 — 0.005° precision

Example: 2000 objects = `3 + (2000 × 14)` = **28,003 bytes** per tick

## Scaled Position Format (0x05, 0x09)

```
[u8 type][u16 count][per entity: u16 id, half x/y/z, u16 rx/ry/rz, half sx/sy/sz]
```

Per entity: **20 bytes** (14 base + 6 half-float scale)

Only used for objects with `syncScale = true`. Objects without it use the standard 14-byte format.

## Variable Update (0x04)

```
[0x04][u16 obj_id][u8 var_count][per var: u8 name_len, name, u8 type, u16 value_len, value]
```

Types: 0 = int (4B), 1 = float (4B), 2 = bool (1B), 3 = string (variable)

## Event / RPC (0x06)

```
[0x06][u16 obj_id][u8 target][u8 name_len][name][u8 param_count][per param: u8 type, u16 len, value]
```

Target: 0 = All, 1 = Authority only, 2 = Proxies only (everyone except authority)

Parameter types: same as variables (int/float/bool/string)

## Animator State (0x07)

```
[0x07][u16 obj_id][i32 state_hash][f32 normalized_time][u8 dirty_count]
[per param: u8 name_len, name, u8 type, 4B value]
```

Param types: 0 = int, 1 = float, 2 = bool, 4 = trigger

## Rigidbody (0x08)

```
[0x08][u16 obj_id][half vx][half vy][half vz][half avx][half avy][half avz]
```

**15 bytes** total. All velocities as half-precision floats.

## Control Messages (0xFF)

```
[0xFF][u8 ctrl_type][payload...]
```

| Ctrl | Direction | Payload | Purpose |
|------|-----------|---------|---------|
| `0x01` | server → client | `u16 client_id` | You are master |
| `0x02` | server → client | `u16 client_id` | You are spectator |
| `0x03` | server → client | `u16 target_client` | Please send snapshot |
| `0x04` | server → client | *(none)* | You are now master (promotion) |
| `0x05` | client → server → others | `u16 object_id, u16 client_id` | Object claimed |
| `0x06` | client → server → others | `u16 object_id, u16 client_id` | Object released |
| `0x08` | master → server → spectators | `u16 object_id, u8 entity_type_len, entity_type` | Spawn object (CTRL_SPAWN) |
| `0x09` | master → server → spectators | `u16 object_id` | Despawn object (CTRL_DESPAWN) |

## Bandwidth Summary

| Objects | Per Tick | At 60Hz |
|---------|----------|---------|
| 100 | 1.4 KB | 84 KB/s |
| 500 | 7 KB | 420 KB/s |
| 1000 | 14 KB | 840 KB/s |
| 2000 | 28 KB | 1.6 MB/s |
| 3000 | 42 KB | 2.5 MB/s |
| 4000 | 56 KB | 3.3 MB/s |

Note: These are worst-case (all objects moving). Delta detection skips stationary objects.

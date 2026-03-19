# Protocol Overview

All messages are **binary**. No JSON on hot paths.

## Message Types

| Byte | Type | Direction | Purpose |
|------|------|-----------|---------|
| `0x01` | Position Update | master → server → spectators | Batch position/rotation for all moving objects |
| `0x02` | Snapshot | master → server → spectators | Full state of all objects (used for late-joiner sync) |
| `0x03` | Interaction Update | any client → server → others | Position/rotation for claimed objects |
| `0x04` | Variable Update | any client → server → others | Custom variable changes *(planned)* |
| `0xFF` | Control | server ↔ client | Role assignment, ownership, commands |

## Position/Interaction Format (0x01, 0x02, 0x03)

```
[u8 type][u16 entity_count][per entity: u16 local_id + 3×f32 position + 3×f32 rotation]
```

Per entity: **26 bytes** (2 + 12 + 12)

Example: 2000 objects = `3 + (2000 × 26)` = **52,003 bytes** per tick

## Control Messages (0xFF)

```
[0xFF][u8 ctrl_type][payload...]
```

| Ctrl Type | Name | Payload | Description |
|-----------|------|---------|-------------|
| `0x01` | You Are Master | — | Server tells client it's the master |
| `0x02` | You Are Spectator | `[u16 client_id]` | Server tells client its ID |
| `0x03` | Send Snapshot | — | Server requests snapshot from master |
| `0x04` | Master Changed | `[u16 new_master_id]` | Broadcast: new master promoted |
| `0x05` | Claim | `[u16 object_id][u16 client_id]` | Object ownership claimed |
| `0x06` | Release | `[u16 object_id][u16 client_id]` | Object ownership released |

## Server Behavior

The server is a **dumb relay**:
- Forwards `0x01` and `0x02` from master to all spectators
- Forwards `0x03` from any client to all other clients
- Caches latest `0x02` snapshot for late joiners
- Tracks object claims in a HashMap for cleanup on disconnect
- Broadcasts `0xFF` control messages for role/ownership changes

The server does NOT understand positions, physics, or game logic.

## Bandwidth

| Objects | Per Tick | At 30 Hz | At 60 Hz |
|---------|----------|----------|----------|
| 100 | 2.6 KB | 78 KB/s | 156 KB/s |
| 500 | 13 KB | 390 KB/s | 780 KB/s |
| 1000 | 26 KB | 780 KB/s | 1.5 MB/s |
| 2000 | 52 KB | 1.5 MB/s | 3.1 MB/s |
| 4000 | 104 KB | 3.1 MB/s | 6.2 MB/s |

Note: Only objects that have **moved** are included in `0x01` updates (delta tracking). Snapshot `0x02` includes everything.

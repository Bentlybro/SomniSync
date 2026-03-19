# Somnium Network v2

Real-time physics object sync for Somnium Space VR worlds via WebSocket relay. Tested with **4000+ objects** — 40x what Somnium's built-in Photon networking handles.

## Architecture

**Dumb relay** — the server doesn't understand game state, it just pipes bytes between clients.

```
Master Client (runs physics)
    ↓ binary position updates (0x01)
Relay Server (Rust, ~340 lines)
    ↓ zero-copy forward
Spectator Clients (apply positions, can interact)
    ↓ interaction updates (0x03) for claimed objects
```

### How it works
1. First client to connect = **master** (runs physics, sends updates)
2. Subsequent clients = **spectators** (receive updates, apply with interpolation)
3. Objects are pre-placed in the scene — both clients sort them deterministically → same local IDs
4. Master disconnects → next client promoted automatically
5. **Spectators can interact** — walk into objects to push them (auto-claim/release)

### Spectator Interaction
- A `PlayerPushDetector` component is attached at runtime to the local player's body
- When the player's CharacterController hits a NetworkObject, it's **auto-claimed**
- Claimed objects run local physics and send position updates (0x03)
- Objects that are pushed into other objects **chain-claim** them (domino effect)
- Once an object settles (velocity ≈ 0), it's **auto-released** back to master authority
- 0.5s cooldown prevents claim/release loops

### Protocol
| Type | Name | Direction | Format |
|------|------|-----------|--------|
| `0x01` | Position Update | master→server→spectators | `[0x01][u16 count][N × (u16 id + 12B pos + 12B rot)]` |
| `0x02` | Snapshot | master→server→spectators | `[0x02][u16 count][N × (u16 id + 12B pos + 12B rot)]` |
| `0x03` | Interaction | any client→server→others | `[0x03][u16 count][N × (u16 id + 12B pos + 12B rot)]` |
| `0xFF` | Control | server↔client | `[0xFF][u8 type][payload]` |

**Control subtypes:**
| Subtype | Name | Payload |
|---------|------|---------|
| `0x01` | You Are Master | — |
| `0x02` | You Are Spectator | `[u16 client_id]` |
| `0x03` | Send Snapshot | — |
| `0x04` | Master Changed | `[u16 new_master_id]` |
| `0x05` | Claim | `[u16 object_id][u16 client_id]` |
| `0x06` | Release | `[u16 object_id][u16 client_id]` |

**2000 objects per tick: ~52KB** (26 bytes/entity)

## Server (Rust)

```bash
cd server
cargo build --release

# Windows
.\target\release\somnium-relay.exe                  # default: ws://0.0.0.0:9090
.\target\release\somnium-relay.exe 0.0.0.0:8080     # custom port

# Linux/macOS
./target/release/somnium-relay                       # default: ws://0.0.0.0:9090
./target/release/somnium-relay 0.0.0.0:8080          # custom port
```

Logs: `RUST_LOG=info` by default. Use `RUST_LOG=debug` to see claim/release events.

## Client (Unity/Somnium)

### Files
| File | Purpose |
|------|---------|
| `NetworkManager.cs` | Connection, protocol, object registration |
| `NetworkObject.cs` | Per-object sync, claiming, physics toggle |
| `NetworkStats.cs` | F3 debug overlay (optional) |
| `PlayerPushDetector.cs` | Auto-attached to player at runtime for interaction |

### Setup
1. Add `NetworkManager` to a GameObject in your scene
2. Add `NetworkObject` to every object you want synced
3. Set `entityType` on each NetworkObject (e.g. "cube", "ball")
4. Optionally add `NetworkStats` for F3 debug overlay

That's it. `PlayerPushDetector` is attached automatically at runtime — no manual setup needed.

### Namespace
```csharp
namespace SomniumSpace.Worlds.bently.networking
```

## Performance
- **4000 objects** tested and working (breakdown point, still being optimized)
- **2000 objects** stable for interactive testing
- **100 objects** was Somnium's Photon limit — we're 40x beyond that
- Zero per-frame cost for interaction detection (event-driven, not polling)
- Server is a ~340-line Rust binary with zero-copy message forwarding

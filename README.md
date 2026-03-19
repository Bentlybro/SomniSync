# Somnium Network v2

Real-time physics object sync for Somnium Space VR worlds via WebSocket relay.

## Architecture

**Dumb relay** — the server doesn't understand game state, it just pipes bytes between clients.

```
Master Client (runs physics)
    ↓ binary position updates (0x01)
Relay Server (Rust, ~200 lines)
    ↓ zero-copy forward
Spectator Clients (apply positions)
```

### How it works
1. First client to connect = **master** (runs physics, sends updates)
2. Subsequent clients = **spectators** (receive updates, apply with interpolation)
3. Objects are pre-placed in the scene — both clients sort them deterministically → same local IDs
4. Master disconnects → next client promoted automatically

### Protocol (3 message types)
| Type | Name | Direction | Format |
|------|------|-----------|--------|
| `0x01` | Position Update | master→server→spectators | `[0x01][u16 count][N × (u16 id + 12B pos + 12B rot)]` |
| `0x02` | Snapshot | master→server→spectators | `[0x02][u16 count][N × (u16 id + 12B pos + 12B rot)]` |
| `0xFF` | Control | server↔client | `[0xFF][u8 type][payload]` |

**800 objects per tick: ~20KB** (26 bytes/entity)

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

git pull && cargo build --release && .\target\release\somnium-relay.exe
```

## Client (Unity/Somnium)

1. Add `NetworkManager` to a GameObject in your scene
2. Add `NetworkObject` to every object you want synced
3. Set `entityType` on each NetworkObject (e.g. "cube", "ball")
4. Optionally add `NetworkStats` for F3 debug overlay

That's it. No configuration needed beyond the server URL.

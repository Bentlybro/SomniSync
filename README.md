# Somnium Network

Real-time physics object sync for Somnium Space VR worlds via WebSocket relay. Tested with **4000+ objects** — 40x what Somnium's built-in Photon networking handles.

## Architecture

**Dumb relay** — the server doesn't understand game state, it just pipes bytes between clients.

```
Master Client (runs physics for all objects)
    ↓ binary position updates (14 bytes/entity, half-precision)
Relay Server (Rust, ~400 lines, 6 modules)
    ↓ zero-copy forward
Spectator Clients (apply positions, can interact)
    ↓ interaction updates (0x03) for claimed objects
```

### How it works
1. First client to connect = **master** (runs physics, sends updates)
2. Subsequent clients = **spectators** (receive updates, apply with interpolation)
3. Objects are pre-placed in the scene — both clients sort them deterministically → same u16 local IDs
4. Master disconnects → next client promoted automatically
5. **Spectators can interact** — walk into objects to push them (auto-claim/release)

### Spectator Interaction
- `PlayerPushDetector` is attached at runtime to the local player's CharacterController
- When the player hits a NetworkObject, it's **auto-claimed** — local physics kicks in instantly
- Claimed objects send position updates (0x03) directly to other clients
- Objects pushed into other objects **chain-claim** them (domino effect)
- Once settled (velocity ≈ 0), objects **auto-release** back to master authority
- 0.5s cooldown prevents claim/release loops

### Protocol (Binary-only, half-precision)

All position data uses half-precision floats (16-bit) and u16-encoded rotation for maximum bandwidth efficiency.

| Type | Name | Direction | Format | Size/entity |
|------|------|-----------|--------|-------------|
| `0x01` | Position Update | master→server→spectators | `[0x01][u16 count][N × (u16 id + 6B half-pos + 6B u16-rot)]` | 14 bytes |
| `0x02` | Snapshot | master→server→spectators | Same as 0x01 | 14 bytes |
| `0x03` | Interaction | any→server→others | Same as 0x01 | 14 bytes |
| `0x04` | Variable Update | any→server→others | `[0x04][u16 obj_id][u8 var_count][per var: u8 name_len, name, u8 type, u16 value_len, value]` | variable |
| `0x06` | Event (RPC) | any→server→others | `[0x06][u16 obj_id][u8 name_len][name][u8 param_count][per param: u8 type, u16 len, value]` | variable |
| `0x07` | Animator State | any→server→others | `[0x07][u16 obj_id][i32 state_hash][f32 norm_time][u8 dirty_count][per param: u8 name_len, name, u8 type, 4B value]` | variable |
| `0x08` | Rigidbody | any→server→others | `[0x08][u16 obj_id][6 × half-float (vel + angvel)]` | 15 bytes |
| `0xFF` | Control | server↔client | `[0xFF][u8 type][payload]` | variable |

**Control subtypes:**

| Subtype | Name | Payload |
|---------|------|---------|
| `0x01` | You Are Master | — |
| `0x02` | You Are Spectator | `[u16 client_id]` |
| `0x03` | Send Snapshot | — |
| `0x04` | Master Changed | `[u16 new_master_id]` |
| `0x05` | Claim | `[u16 object_id][u16 client_id]` |
| `0x06` | Release | `[u16 object_id][u16 client_id]` |

**Bandwidth:** 2000 objects at 30Hz = ~28KB/tick (14 bytes/entity). 3000 objects confirmed smooth.

## Components

| Component | Purpose | Protocol | Required |
|-----------|---------|----------|----------|
| **NetworkManager** | Connection, protocol, object registration, master/spectator logic | — | Yes (1 per scene) |
| **NetworkObject** | Per-object position/rotation sync, ownership, claiming, kill zone | 0x01, 0x02, 0x03 | Yes (per object) |
| **NetworkVariables** | Sync custom data (int, float, bool, string) per object | 0x04 | Optional |
| **NetworkEvents** | Fire-and-forget RPCs between clients | 0x06 | Optional |
| **NetworkAnimator** | Sync Animator state, parameters, and triggers | 0x07 | Optional |
| **NetworkRigidbody** | Sync velocity + angular velocity (half-float) | 0x08 | Optional |
| **NetworkParticle** | Sync particle play/stop + properties | via 0x04, 0x06 | Optional |
| **NetworkTimer** | Authority-driven countdowns with events | via 0x04 | Optional |
| **PlayerPushDetector** | Auto-attached collision detection for interaction | — | Auto |
| **NetworkStats** | F3 debug overlay (FPS, bandwidth, status) | — | Optional |
| **PlayerDebugMarker** | Scene-view-only player capsule visualization | — | Optional |

## NetworkObject Settings

| Field | Default | Description |
|-------|---------|-------------|
| `entityType` | `"default"` | Type string for deterministic sorting |
| `killFloorY` | `-500` | Y position below which out-of-bounds action triggers |
| `outOfBoundsAction` | `ResetToSpawn` | What to do when object falls below killFloorY: `ResetToSpawn`, `Deactivate`, or `Release` |

### Kill Zone
Objects falling below `killFloorY` are automatically handled based on `outOfBoundsAction`:
- **ResetToSpawn** (default) — teleport back to original position/rotation
- **Deactivate** — disable the GameObject
- **Release** — release ownership (master will reclaim and reset)

Invalid positions (NaN/Infinity) are rejected and the object snaps to last known good position.

## NetworkVariables

```csharp
var netVars = GetComponent<NetworkVariables>();

// Define variables (in Start/Awake)
netVars.DefineVariable("health", VarType.Float, SyncMode.OnChange, persist: true);
netVars.DefineVariable("isOpen", VarType.Bool, SyncMode.Immediate, persist: true);

// Set values (authority only)
netVars.SetFloat("health", 75f);
netVars.SetBool("isOpen", true);

// Read values
float hp = netVars.GetFloat("health");
bool open = netVars.GetBool("isOpen");

// Listen for changes
netVars.OnValueChanged += (string name, VarType type) => {
    Debug.Log($"{name} changed to {netVars.GetFloat(name)}");
};
```

**Sync modes:** `OnChange` (batched, bandwidth-efficient) or `Immediate` (sent instantly).

## NetworkEvents (RPCs)

```csharp
var netEvents = GetComponent<NetworkEvents>();

// Register handler
netEvents.RegisterEvent("Explode", (NetworkEventData data) => {
    float radius = data.GetFloat(0);
    SpawnExplosion(radius);
});

// Fire event (sent to all other clients)
netEvents.FireEvent("Explode", new EventParam(3.5f));
```

Events are fire-and-forget — late joiners don't receive past events.

## NetworkAnimator

Attach alongside a Unity `Animator` component. Automatically syncs:
- Current state hash + normalized time
- All Animator parameters (float, int, bool)
- Trigger detection

## NetworkRigidbody

Syncs Rigidbody velocity and angular velocity using half-precision floats. 15 bytes per update. Authority-driven with configurable velocity/angular thresholds.

## NetworkParticle

Built on NetworkEvents + NetworkVariables. Syncs:
- Play/Stop commands (via events)
- Emission rate, start color, start speed, start size (via variables)

## NetworkTimer

Authority-driven countdown timer using NetworkVariables. Features:
- `OnStarted`, `OnFinished`, `OnTick` events
- Loop and autoStart options
- Survives master transfer (state synced via variables)

## Server (Rust)

Modular Rust server using tokio + tokio-tungstenite:

```
server/src/
├── main.rs       — entry point, startup, graceful shutdown (Ctrl+C)
├── protocol.rs   — message constants, control message builders
├── room.rs       — room state, ownership tracking, claim validation
├── client.rs     — Client struct, send/broadcast helpers
├── handler.rs    — connection handler, message dispatch
└── config.rs     — server config (port, future persistence toggle)
```

### Build & Run

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

Logs: `RUST_LOG=info` by default. Use `RUST_LOG=debug` for claim/release events.

The server caches the latest snapshot for instant late-joiner sync. Graceful shutdown on Ctrl+C.

## Client Setup (Unity/Somnium)

### File Structure
```
client/Unity/
├── Core/
│   ├── NetworkManager.cs    — connection, protocol, master/spectator logic
│   ├── NetworkObject.cs     — per-object sync, ownership, kill zone
│   └── Protocol.cs          — binary encoding (half-float, u16 rotation)
├── Components/
│   ├── NetworkVariables.cs  — custom variable sync
│   ├── NetworkEvents.cs     — fire-and-forget RPCs
│   ├── NetworkAnimator.cs   — Animator state sync
│   ├── NetworkRigidbody.cs  — velocity sync
│   ├── NetworkParticle.cs   — particle system sync
│   ├── NetworkTimer.cs      — countdown timer
│   └── PlayerPushDetector.cs— auto-attached push detection
└── Debug/
    ├── NetworkStats.cs      — F3 overlay
    └── PlayerDebugMarker.cs — scene-view player viz
```

### Quick Start
1. Add `NetworkManager` to a GameObject — set `serverUrl`
2. Add `NetworkObject` to every synced object — set `entityType`
3. (Optional) Add `NetworkStats` for debug overlay
4. Done. `PlayerPushDetector` attaches automatically at runtime.

### Namespace
```csharp
using SomniumSpace.Worlds.bently.networking;
```

## Performance

| Objects | Status | Notes |
|---------|--------|-------|
| **4000** | Working (breakdown point) | 40x Photon's ~100 limit |
| **3000** | Smooth | Half-precision optimization |
| **2000** | Stable | Interactive testing baseline |
| **800** | Production-ready | 400 cubes + 400 balls stacked |

- 14 bytes per entity per tick (half-precision position + u16 rotation)
- Zero per-frame cost for interaction detection (event-driven)
- Server: zero-copy message forwarding, ~400 lines of Rust
- Periodic resync snapshots prevent drift (configurable interval)
- Resync skipped when world is idle (no objects moving)

## Somnium Space Notes

- Uses `System.Net.WebSockets` (no third-party libs allowed)
- Player hierarchy: `Original Player: [Name]` → `Player.NonXR.Body(Clone)` → `NonXR.Body` (CharacterController)
- VR variant: `XR.Body` instead of `NonXR.Body`
- Remote players: `Simulator Player:` prefix
- Objects start with rigidbodies frozen (kinematic) — physics enabled per-object as needed

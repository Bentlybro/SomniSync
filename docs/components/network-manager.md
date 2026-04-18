# NetworkManager

The central networking component. One per scene.

## Inspector Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `serverUrl` | string | `ws://localhost:9090` | WebSocket server URL |
| `roomId` | string | `""` | Room name; clients in the same room share a physics world. Empty = `default`. |
| `sendRate` | int | `60` | Held-object position updates per second |
| `playerPosSendRate` | int | `10` | Local player-position send rate for server-side AOI |
| `positionThreshold` | float | `0.01` | Min position change (m) to include a held object in an update |
| `rotationThreshold` | float | `1` | Min rotation change (°) to include a held object in an update |
| `localPlayer` | Transform | *auto* | Your player transform; left blank auto-detects Somnium rig |
| `autoAttachPushDetector` | bool | `false` | Attach `PlayerPushDetector` on contact with the resolved local player |
| `networkedPrefabs` | List<GameObject> | `[]` | Prefabs eligible for runtime spawn via `CTRL_SPAWN` — order = prefab index |

## Key Properties (Code)

```csharp
// Singleton access
NetworkManager.Instance

// Connection state
bool IsConnected
bool IsNetworkReady    // handshake done, safe to use network features
bool IsPhysicsReady    // server physics is live (first SCENE_INIT landed)
ushort ClientId        // assigned by server (1..65535)

// Shared clock — same value across all clients in the room
double ServerTime             // seconds, NTP-refined via 1 Hz ping/pong + EMA
ulong  SceneReadyServerMs     // unix ms when physics went live in this room

// Runtime spawn callbacks
event Action<NetworkObject, int> OnObjectSpawned
event Action<NetworkObject>      OnObjectDespawned

// Lifecycle events
event Action OnNetworkReady
event Action OnDisconnected
```

## How It Works

1. On `Start()`, NetworkManager connects to the WebSocket at `serverUrl/roomId`.
2. It collects every `NetworkObject` in the scene and sorts them deterministically — every client gets the same local IDs.
3. The server replies with `CTRL_WELCOME` containing the client ID, server unix time, and the room's shared `scene_ready_ms`.
4. **If we're the first client in an empty room**, the welcome has `needs_scene_init = 1`; NetworkManager replies with `MSG_SCENE_INIT` describing every `NetworkObject`'s physics properties. The server builds its Rapier world from that and broadcasts `CTRL_PHYSICS_READY`.
5. From there the server is authoritative. We send: player position (10 Hz AOI), held-object updates (60 Hz while grabbing), events / variables / animator state, time-sync pings.
6. We receive: `MSG_POSITION_VELOCITY` (20 Hz authoritative positions), claim / release broadcasts, spawns / despawns, event relays.

No master/spectator distinction — the server is always the authority. Clients come and go without handoffs.

## Auto-behaviours

- **Local player resolution** — priority is `localPlayer` field → `SetLocalPlayer()` call (e.g. from `SomniumBridgeIntegration`) → auto-detection of Somnium rigs (`Original Player:` root with `NonXR.Body` / `XR.Body` character controllers).
- **Optional PlayerPushDetector** — auto-attaches when `autoAttachPushDetector` is true. Off by default; the server's Rapier player capsule already handles continuous push physics from normal movement.
- **NTP-style clock sync** — pings every 1 s, blends each sample into the offset via EMA (α = 0.35). Handles server-side clock drift / NTP adjustments without ever locking onto a stale sample.
- **Reconnect** — on disconnect, in-flight claims are released, scene-ready state is cleared, and the connection loop retries every `reconnectDelay` seconds.

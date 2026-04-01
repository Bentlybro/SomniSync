# NetworkManager

The central networking component. One per scene.

## Inspector Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `serverUrl` | string | `ws://localhost:9090` | WebSocket server URL |
| `sendRate` | int | 30 | Position updates per second |
| `positionThreshold` | float | 0.001 | Min position change to send update (meters) |
| `rotationThreshold` | float | 0.1 | Min rotation change to send update (degrees) |

## Key Properties (Code)

```csharp
// Singleton access
NetworkManager.Instance

// Connection state
bool IsConnected
bool IsMaster        // true if this client runs physics
ushort ClientId      // assigned by server (e.g. 1, 2, 3...)

// Object access
List<NetworkObject> Objects  // all registered NetworkObjects

// Runtime object registration
void RegisterObject(NetworkObject obj)    // register a runtime-spawned object
void UnregisterObject(NetworkObject obj)  // unregister before destroying
```

## How It Works

1. On `Start()`, connects to the WebSocket server
2. Collects and sorts all `NetworkObject` components deterministically
3. Server assigns role: master or spectator
4. Master sends position updates at `sendRate` Hz
5. Spectators receive and apply updates with interpolation
6. Late joiners receive a cached snapshot for instant sync (includes all persistent NetworkVariables state)
7. All outgoing sends are queued — only one WebSocket send is in-flight at a time, preventing message loss on busy frames

## Runtime Object Registration

Objects present in the scene at startup are registered automatically. For objects spawned at runtime (e.g. instantiated prefabs), call `RegisterObject` / `UnregisterObject` explicitly:

```csharp
// Spawn a networked object at runtime
GameObject go = Instantiate(myPrefab);
NetworkObject netObj = go.GetComponent<NetworkObject>();
NetworkManager.Instance.RegisterObject(netObj);

// Destroy a networked object at runtime
NetworkManager.Instance.UnregisterObject(netObj);
Destroy(go);
```

When the master calls `RegisterObject`, a `CTRL_SPAWN` (0x08) control message is broadcast to all spectators so they can instantiate the matching object. `UnregisterObject` sends `CTRL_DESPAWN` (0x09). Late joiners receive the current registered object list as part of the snapshot.

## Auto-Behaviors

- **Push Detector**: Automatically attaches `PlayerPushDetector` to the local player's body (NonXR.Body / XR.Body) so spectators can interact with objects
- **Master Promotion**: If master disconnects, NetworkManager handles the role switch seamlessly

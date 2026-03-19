# NetworkManager

The central networking component. One per scene.

## Inspector Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `serverUrl` | string | `wss://simple-sync.bentlybro.com` | WebSocket server URL |
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
```

## How It Works

1. On `Start()`, connects to the WebSocket server
2. Collects and sorts all `NetworkObject` components deterministically
3. Server assigns role: master or spectator
4. Master sends position updates at `sendRate` Hz
5. Spectators receive and apply updates with interpolation
6. Late joiners receive a cached snapshot for instant sync

## Auto-Behaviors

- **Push Detector**: Automatically attaches `PlayerPushDetector` to the local player's body (NonXR.Body / XR.Body) so spectators can interact with objects
- **Master Promotion**: If master disconnects, NetworkManager handles the role switch seamlessly

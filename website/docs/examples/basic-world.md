# Example: Basic Physics World

The simplest setup — sync physics objects between players.

## Scene Setup

1. Create your world with objects (cubes, balls, etc.)
2. Add `Rigidbody` to every object you want physics on
3. Add `NetworkObject` to each one, set `entityType` (e.g. `"cube"`)
4. Add one `NetworkManager` to an empty GameObject
5. *(Optional)* Add `NetworkStats` for debug info (F3 to toggle)

## Hierarchy

```
Scene
├── [NetworkManager]
│   └── NetworkManager (serverUrl: ws://localhost:9090, roomId: "my-world")
│
├── [DebugUI]                          (optional)
│   └── NetworkStats
│
├── Floor                               (no NetworkObject — static)
│   └── BoxCollider
│
├── Cube_001
│   ├── Rigidbody
│   └── NetworkObject (entityType: "cube")
│
├── Cube_002
│   ├── Rigidbody
│   └── NetworkObject (entityType: "cube")
│
├── Ball_001
│   ├── Rigidbody (mass: 0.5)
│   ├── SphereCollider
│   └── NetworkObject (entityType: "ball")
│
└── Ball_002
    ├── Rigidbody (mass: 0.5)
    ├── SphereCollider
    └── NetworkObject (entityType: "ball")
```

## What happens

- The first client describes supported colliders to the relay; the relay runs physics for the room.
- All clients receive authoritative position updates for moving objects. Resting bodies stop sending updates.
- Each client sends its local player position for area-of-interest filtering when a local player transform is available.
- Grabbing requires your interaction code to call `NetworkObject.Grab()` and `Release()`. Walking into a pushable body can affect server physics through the player capsule; the optional `PlayerPushDetector` adds a one-shot impulse.
- The relay continues simulating if the first client disconnects. There is no client master role.

## Per-Object Control

Don't want players pushing certain objects?

```
Wall_001
├── Rigidbody (isKinematic: true)
└── NetworkObject (entityType: "wall", pushable: false, grabbable: false)
```

## Tips

- `entityType` affects deterministic sorting — use consistent names across clients
- All clients must have identical object placement in their scenes
- Static geometry (floors, walls without physics) doesn't need `NetworkObject`
- Start with `smoothing: Medium` — adjust per-object if needed

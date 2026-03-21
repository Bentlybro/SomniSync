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

## What Happens

- First player to join becomes **master** (runs physics for everything)
- Other players join as **spectators** (see synced positions, can interact)
- Walk into any object → auto-claimed → local physics → auto-released when settled
- Master disconnects → next player auto-promoted, no interruption
- Objects start frozen (kinematic) — physics enabled per-object as needed

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

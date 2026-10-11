# Example: Physics Playground

A room full of throwable objects, destructible stacks, and chain reactions. Shows off the core physics networking.

## Scene Layout

```
Playground
├── [NetworkManager] (roomId: "playground")
│
├── --- Throwables ---
│   ├── Ball_001..010
│   │   ├── Rigidbody (mass: 1)
│   │   ├── SphereCollider
│   │   └── NetworkObject (entityType: "ball", pushable: true, grabbable: true)
│   │
│   └── Bottle_001..005
│       ├── Rigidbody (mass: 0.3)
│       ├── BoxCollider (approximating the bottle)
│       └── NetworkObject (entityType: "bottle", pushable: true, grabbable: true)
│
├── --- Destructible Stack ---
│   └── Crate_001..050                           ← stacked wall of crates
│       ├── Rigidbody (mass: 2)
│       ├── BoxCollider
│       └── NetworkObject (entityType: "crate")
│
├── --- Bowling Alley ---
│   ├── BowlingBall
│   │   ├── Rigidbody (mass: 5)
│   │   ├── SphereCollider
│   │   └── NetworkObject (entityType: "bowlingball", pushable: true, grabbable: true)
│   │
│   └── Pin_001..010
│       ├── Rigidbody (mass: 0.5)
│       └── NetworkObject (entityType: "pin")
│
└── --- Static ---
    ├── Floor (BoxCollider, no NetworkObject)
    ├── Walls (BoxCollider, no NetworkObject)
    └── Ramps (BoxCollider, no NetworkObject)
```

## Key Decisions

**Throwables use dynamic `Rigidbody` + `NetworkObject`** — the server simulates flight in Rapier and broadcasts authoritative pose/velocity snapshots.

**Crates use the same setup** — there are 50 of them and they mostly sit still in a stack. Sleeping bodies are cheap because the server stops broadcasting them until they wake or settle.

**Static geometry has no networking components** — floors, walls, ramps don't move.

## What It Looks Like

1. Player A grabs a ball → throws it at the crate wall
2. Ball flight is server-simulated and broadcast through `MSG_POSITION_COMPACT` for capable clients
3. Ball hits first crate → chain claim triggers → entire stack claimed by Player A
4. Crates scatter everywhere under server physics
5. Crates settle → sleeping bodies stop broadcasting
6. Player B sees the whole thing in real-time

## Resetting the alley

The relay is the physics authority. Directly moving an unclaimed pin's Unity Transform will **not** reset its server body. This version has no general server-side reset/teleport command for baked scene bodies. To build an authoritative reset, add a validated server command that moves those Rapier bodies and broadcasts their new poses, or despawn and respawn runtime-spawned pins. Do not use a local-only transform loop for a shared reset.

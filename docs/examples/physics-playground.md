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
│       ├── MeshCollider (convex: true)
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
2. Ball flight is server-simulated and broadcast through `MSG_POSITION_VELOCITY`
3. Ball hits first crate → chain claim triggers → entire stack claimed by Player A
4. Crates scatter everywhere under server physics
5. Crates settle → sleeping bodies stop broadcasting
6. Player B sees the whole thing in real-time

## Bowling Reset

```csharp
public class BowlingAlley : MonoBehaviour
{
    public Transform[] pinSpawns;
    public NetworkObject[] pins;
    private NetworkEvents _events;

    void Start()
    {
        _events = GetComponent<NetworkEvents>();
        _events.On("reset", () => ResetPins());
    }

    public void ResetPins()
    {
        _events.Send("reset");
        for (int i = 0; i < pins.Length; i++)
        {
            pins[i].transform.position = pinSpawns[i].position;
            pins[i].transform.rotation = pinSpawns[i].rotation;
            var rb = pins[i].GetComponent<Rigidbody>();
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
    }
}
```

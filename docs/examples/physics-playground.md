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
│   │   ├── NetworkObject (entityType: "ball", pushable: true, grabbable: true)
│   │   └── NetworkRigidbody                    ← smooth flight when thrown
│   │
│   └── Bottle_001..005
│       ├── Rigidbody (mass: 0.3)
│       ├── MeshCollider (convex: true)
│       ├── NetworkObject (entityType: "bottle", pushable: true, grabbable: true)
│       └── NetworkRigidbody
│
├── --- Destructible Stack ---
│   └── Crate_001..050                           ← stacked wall of crates
│       ├── Rigidbody (mass: 2)
│       ├── BoxCollider
│       └── NetworkObject (entityType: "crate")  ← no NetworkRigidbody (bulk)
│
├── --- Bowling Alley ---
│   ├── BowlingBall
│   │   ├── Rigidbody (mass: 5)
│   │   ├── SphereCollider
│   │   ├── NetworkObject (entityType: "bowlingball", pushable: true, grabbable: true)
│   │   └── NetworkRigidbody
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

**Throwables get `NetworkRigidbody`** — balls and bottles fly through the air, velocity sync makes them smooth.

**Crates do NOT get `NetworkRigidbody`** — there are 50 of them and they mostly sit still in a stack. Position-only sync is fine. When a player bowls through them, `NetworkObject` handles the cascade via chain claiming.

**Static geometry has no networking components** — floors, walls, ramps don't move.

## What It Looks Like

1. Player A grabs a ball → throws it at the crate wall
2. Ball flight is smooth (NetworkRigidbody syncs velocity)
3. Ball hits first crate → chain claim triggers → entire stack claimed by Player A
4. Crates scatter everywhere (position-only sync, no velocity)
5. Crates settle → auto-release back to master
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

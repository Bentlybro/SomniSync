# NetworkObject

Required on every GameObject you want synced across the network. Handles position / rotation / scale sync, ownership (claim/release), and player interaction.

**One rule:** if a client *holds* an object, their `NetworkObject` is the authority for its pose. Otherwise the server is. Held-object updates go out at your `NetworkManager.sendRate` (default 60 Hz); server broadcasts come back at 20 Hz.

```
GameObject "Sword"
├── Rigidbody              (any config — kinematic gets auto-set at Awake so local physics doesn't drift)
├── Collider               (for interaction + server physics)
├── NetworkObject          ← required
└── <optional components>  NetworkVariables, NetworkAnimator, ...
```

## Inspector properties

| Property | Type | Default | Description |
|---|---|---|---|
| `entityType` | string | `"object"` | Category tag used for deterministic sorting across clients. Pick something stable like `"cube"`, `"sword"`, `"door"`. |
| `syncScale` | bool | `false` | Sync `transform.localScale` in addition to pos/rot. Adds 6 B per update. Off unless you need it. |
| `pushable` | bool | `true` | Players walking into this object can push it (via `PlayerPushDetector`). |
| `grabbable` | bool | `true` | Players can grab this object for custom grab systems. |
| `smoothing` | enum | `Medium` | Interpolation aggressiveness. See table below. |

### Smoothing modes

| Mode | Behaviour | Use for |
|---|---|---|
| `None` | Snap to the latest server position. | Teleporters, cutscene anchors. |
| `Low` | ~33 ms lerp. | Fast projectiles, bullets. |
| `Medium` | ~50 ms lerp. *(default)* | Most props. |
| `High` | ~100 ms lerp. | Slow heavy things — doors, platforms, boulders. |

## Runtime API

### Claim state

```csharp
public bool    IsClaimedLocally  { get; }         // This client drives the object
public bool    IsClaimedByRemote { get; }         // Some other client drives it
public ushort  ClaimedByClient   { get; }         // 0 = server, else the cid
public bool    HasAuthority();                     // True if we can write sibling state
                                                  //   (NetworkVariables, NetworkAnimator, ...)
```

`HasAuthority()` is the gate you check before writing to `NetworkVariables` / `NetworkAnimator` / `NetworkTimer` on this object. Rule: authority if we hold it OR nobody does. Grabbed items are written by the grabber; free-floating objects accept writes from anyone.

### Last release pose

Populated when the server broadcasts `CTRL_RELEASE` for this object (i.e. the previous holder let go). Deterministic subsystems like `NetworkNPC` use it to re-anchor.

```csharp
public Vector3     LastReleasePos      { get; }
public Quaternion  LastReleaseRot      { get; }
public ulong       LastReleaseServerMs { get; }
```

### Spawn timestamp

Server-authoritative birth time of this object. For scene-baked objects this is `NetworkManager.SceneReadyServerMs`; for runtime spawns it's set from the `CTRL_SPAWN` message.

```csharp
public ulong SpawnServerMs { get; }   // 0 = not ready yet (defer work this tick)
```

### Identity

```csharp
public ushort LocalId;   // Deterministic ID assigned by NetworkManager at scene ready
```

### Public methods

```csharp
// Request ownership. Optimistic — call sites like PlayerPushDetector
// call this on contact and assume success; if rejected, OnClaimRejected
// unwinds cleanly.
void Grab(bool enableLocalPhysics = true);

// Release ownership. Server will broadcast an authoritative release pose
// + timestamp that every client re-anchors to.
void Release();

// Called automatically by PlayerPushDetector on CharacterController contact.
// No need to call manually — listed here for completeness.
void OnPlayerPush();
```

## Common patterns

### Sync any GameObject

Drop `NetworkObject` on it. Done.

```
GameObject "Crate"
├── Rigidbody
├── BoxCollider
└── NetworkObject  (entityType: "crate")
```

### Grab & throw

```csharp
// Typical VR interaction: on trigger-press, attach to hand
public class HandGrab : MonoBehaviour
{
    public Transform hand;
    NetworkObject _held;

    void Update()
    {
        if (Input.GetButtonDown("Grab") && TryFindNearby(out _held))
            _held.Grab(enableLocalPhysics: false);  // we're driving pose

        if (Input.GetButtonUp("Grab") && _held != null)
        {
            _held.Release();
            _held = null;
        }

        if (_held != null && _held.IsClaimedLocally)
            _held.transform.position = hand.position;  // overwritten every frame
    }

    bool TryFindNearby(out NetworkObject hit) { /* raycast / overlap */ }
}
```

The held-object update path (`MSG_INTERACTION_UPDATE`) is automatic — NetworkManager ships your transform pose at `sendRate` Hz while a NetworkObject you own is moving.

### Claim before writing sibling state

```csharp
// On some trigger — change a networked variable
var vars = obj.GetComponent<NetworkVariables>();
if (obj.HasAuthority())
    vars.Set("health", newHealth);     // broadcast to all clients
// else: silently ignore, or grab first
```

### React to remote events

```csharp
// Subscribe to sibling NetworkEvents
var events = obj.GetComponent<NetworkEvents>();
events.OnEvent("opened", args => {
    // some other client opened this door
    doorAnim.Play("open");
});
```

### Wait for a valid spawn timestamp

For code that depends on `SpawnServerMs` (e.g. deterministic motion anchored to scene start):

```csharp
void Update()
{
    var obj = GetComponent<NetworkObject>();
    ulong spawnMs = obj.SpawnServerMs;
    if (spawnMs == 0) return;   // welcome / physics-ready not in yet — try next tick

    double spawnSec = spawnMs / 1000.0;
    double t = NetworkManager.Instance.ServerTime - spawnSec;
    // ...use t as seconds-since-birth, synced across all clients
}
```

## Extending NetworkObject

The class is a `partial` split across three files — `NetworkObject.cs` (core), `NetworkObject.Interaction.cs` (claim/release/push), `NetworkObject.Interpolation.cs` (pose application). `Awake` is `protected virtual`, so subclasses layer on top cleanly:

```csharp
public class BombObject : NetworkObject
{
    public float fuseSeconds = 3f;
    float _armedAt = -1;

    protected override void Awake()
    {
        base.Awake();               // keep NetworkObject's Rigidbody capture etc.
        // your initialisation here
    }

    void Update()
    {
        // Only the holder drives the bomb.
        if (!IsClaimedLocally) return;
        if (_armedAt < 0) _armedAt = Time.time;
        if (Time.time - _armedAt > fuseSeconds)
            GetComponent<NetworkEvents>().Send("boom");
    }
}
```

For customising smoothing itself (`ApplyUpdate` / interpolation), the interpolation logic lives in the partial — reach for it if you need a different curve (spring, critically-damped, fixed-duration-regardless-of-rate).

## Don't

- **Don't** set a unique `entityType` per instance ("cube_001", "cube_002"). The sort has to match on every client, so use a *category* tag — instance distinction comes from position / name.
- **Don't** call `Grab()` on a cooldown loop. The server rate-limits and legitimate claim attempts can end up rejected. Use it in response to actual input.
- **Don't** manually set `transform.position` on an object you don't hold. The next server broadcast will snap it back.
- **Don't** assume `SpawnServerMs` is non-zero on the first `Awake`. It populates from `CTRL_WELCOME` / `CTRL_PHYSICS_READY`, which may arrive frames later. Guard.

## Related

- [**NetworkManager**](./network-manager) — connection, registration, shared clock
- [**NetworkNPC**](./network-npc) — one-component AI using NetworkObject for its body
- [**NetworkVariables**](./network-variables) — per-object custom state sync
- [**NetworkEvents**](./network-events) — per-object RPCs
- [**PlayerPushDetector**](./player-push-detector) — the auto-attached component that invokes `Grab()` on walking contact

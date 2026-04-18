# NetworkNPC

One component, one NPC. Deterministic wander + optional chase-aggro against the local player. Designed to scale — idle NPCs cost **zero bytes** regardless of count because their wander path is computed locally from a shared server clock.

Attach to any GameObject that should behave like an NPC. Requires a sibling **NetworkObject**.

```
GameObject "WanderCube"
├── Rigidbody             (any config — forced kinematic by NetworkNPC)
├── Collider
├── NetworkObject
└── NetworkNPC            ← this page
```

## What it does

- Keeps the object kinematic on the server (so Rapier doesn't fight the AI with gravity / tumbling).
- **Wander** — every client computes `pos = f(t, seed)` from `NetworkManager.ServerTime - SpawnServerMs` and a per-NPC seed. Same inputs on every client → same output. No broadcasts for idle NPCs.
- **Chase** (optional) — when the local player enters `aggroRadius`, the first client to notice grabs the NPC via `NetworkObject.Grab()` and drives it toward the player. The claim system ensures only one client is authoritative at a time.
- On release, re-anchors the wander path to the server's authoritative release pose + timestamp, so after a chase every client lands on an identical future wander.

## Inspector properties

| Property | Default | Description |
|---|---|---|
| `canWander` | `true` | Wander within a radius while idle. Off for stationary sentinels. |
| `wanderRadius` | `8` m | How far from its spawn point the NPC drifts. |
| `wanderSpeed` | `2` m/s | Movement speed while wandering. |
| `pauseMin` / `pauseMax` | `0.5` / `2.5` s | Pause duration at each waypoint (deterministic from seed). |
| `canChase` | `false` | Chase the local player when close. |
| `aggroRadius` | `10` m | Enter chase at this distance. |
| `deaggroRadius` | `14` m | Exit chase at this distance. Must be > `aggroRadius` (hysteresis). |
| `chaseSpeed` | `4` m/s | Movement speed while chasing. Usually > wanderSpeed. |
| `minContactDistance` | `0.8` m | Stop this close to the player. |
| `reclaimCooldown` | `1.5` s | Wait this long after a rejected claim before trying again. |
| `faceDirection` | `true` | Rotate to face motion / target. |
| `turnSpeed` | `540` °/s | Max rotation rate. 0 = instant. |
| `seedOverride` | `-1` | Random seed. `-1` uses `NetworkObject.LocalId` so each NPC has a unique but client-agreed path. |

## Runtime API

Read-only debug accessors used by `NetworkDebug` to compare inter-client state:

```csharp
public Vector3 DebugOrigin           { get; }
public double  DebugWanderEpoch      { get; }
public ulong   DebugAnchoredReleaseMs { get; }
```

Everything else is driven from the inspector + the wander / chase state machine internally.

## Recipes

### Pure wanderer

```
NetworkNPC:
  canWander    = true
  canChase     = false
  wanderRadius = 8
```

That's it. Zero code, zero bandwidth, identical positions on every client.

### Monster

```
NetworkNPC:
  canWander     = true
  canChase      = true
  aggroRadius   = 12
  deaggroRadius = 18
  chaseSpeed    = 5
```

Hangs around its spawn point until a player comes within 12 m, then chases until the player escapes to 18 m.

### Stationary turret

```
NetworkNPC:
  canWander = false
  canChase  = true
```

Doesn't move until aggro'd. On chase, it homes in.

### Fast scout (short aggro, long leash)

```
NetworkNPC:
  canWander     = true
  wanderRadius  = 20
  canChase      = true
  aggroRadius   = 6
  deaggroRadius = 30     // gives up only when player is very far
  chaseSpeed    = 8
```

## Extending: custom AI

`NetworkNPC` exposes key hooks so you subclass instead of forking:

```csharp
public class PatrollingGuard : NetworkNPC
{
    public Transform[] waypoints;
    public float secondsPerSegment = 3f;

    // Replace the wander sampler. `t` is seconds since this NPC's
    // SpawnServerMs — every client agrees on it, so every client
    // computes the same position without any broadcast.
    protected override Vector3 SampleWander(double t, int seed, out Vector3 forward)
    {
        if (waypoints.Length < 2) { forward = Vector3.zero; return _origin; }
        int i    = (int)(t / secondsPerSegment) % waypoints.Length;
        int next = (i + 1) % waypoints.Length;
        float segT = (float)((t % secondsPerSegment) / secondsPerSegment);

        Vector3 pos = Vector3.Lerp(waypoints[i].position, waypoints[next].position, segT);
        forward = (waypoints[next].position - waypoints[i].position).normalized;
        return pos;
    }
}
```

Chase behaviour (aggro radius, grab/release handshake, server-authoritative re-anchor) still runs — you only overrode the idle-path sampler.

### Adding per-frame logic without losing base behaviour

```csharp
public class RagingMonster : NetworkNPC
{
    public Material angryMaterial;
    Material _originalMat;
    Renderer _rend;

    protected override void Awake()
    {
        base.Awake();
        _rend = GetComponent<Renderer>();
        _originalMat = _rend.sharedMaterial;
    }

    protected override void Update()
    {
        base.Update();   // run wander + chase

        // Switch material based on whether we're actively chasing.
        _rend.sharedMaterial = _netObj.IsClaimedLocally
            ? angryMaterial
            : _originalMat;
    }
}
```

`_netObj` is `protected` so subclasses can read its claim state; `_origin`, `_wanderEpoch`, `_anchoredReleaseMs` are all accessible if you need them.

### Custom facing (head-lerp, spring)

```csharp
public class HeadLookNPC : NetworkNPC
{
    public Transform head;
    public float headLerpSpeed = 10f;

    protected override void ApplyFacing(Vector3 forward)
    {
        base.ApplyFacing(forward);     // body still rotates normally
        if (head != null && forward.sqrMagnitude > 0.001f)
            head.rotation = Quaternion.Slerp(
                head.rotation,
                Quaternion.LookRotation(forward),
                headLerpSpeed * Time.deltaTime);
    }
}
```

## How determinism works (under the hood)

- **Wander anchor:** `_wanderEpoch` = `NetworkObject.SpawnServerMs / 1000.0`. For scene-baked NPCs this is `SceneReadyServerMs` (shared scene epoch). For runtime spawns it's the server unix ms stamped into `CTRL_SPAWN`. **All clients read the same value** → they compute positions from the same slice of the same deterministic path.
- **Seeded RNG:** `seedOverride >= 0 ? seedOverride : NetworkObject.LocalId`. LocalId is itself deterministic from the sorted scene, so every client computes the same seed.
- **Re-anchor after chase:** when `NetworkObject.LastReleaseServerMs` advances (server broadcast a CTRL_RELEASE), we snap `_origin = LastReleasePos` and `_wanderEpoch = LastReleaseServerMs / 1000`. Everyone now wanders from the same authoritative post-chase position.

If NPCs disagree visually between clients, it's almost always one of:
1. `SpawnServerMs` not populated yet (welcome / physics-ready hasn't arrived) — wait a tick.
2. Different `seedOverride` values between clients — shouldn't happen if you don't set it, or set it to a constant on the prefab.
3. Some subclass code writing `transform.position` outside `SampleWander`.

## Diagnostics

Drop [NetworkDebug](./) on a scene object to dump per-NPC state (position, origin, epoch, claim flags) every second — optionally to a CSV file. `tools/netdbg_diff.py` compares two CSVs and reports per-NPC position delta between clients.

## Don't

- **Don't** write `transform.position` from outside NetworkNPC unless you're in an override that knows what it's doing — the wander sampler clobbers it every frame.
- **Don't** set `aggroRadius >= deaggroRadius`. You'll flicker in/out of chase at the boundary.
- **Don't** reuse the same `seedOverride` on multiple NPCs unless you want them marching in identical paths.
- **Don't** use `mgr.ServerTime` as a wander epoch in a subclass — that's exactly the bug we fixed by introducing `SpawnServerMs`. Two clients joining at different times would anchor to different moments and drift.

## Related

- [**NetworkObject**](./network-object) — required sibling; provides claim/release + SpawnServerMs
- [**NetworkManager**](./network-manager) — shared clock, SceneReadyServerMs
- [**NetworkVariables**](./network-variables) — attach to add health, state, whatever
- [**NetworkEvents**](./network-events) — fire RPCs from the NPC (death, hit, sound)
- [**NetworkDebug**](./) — per-tick diagnostic CSV dump

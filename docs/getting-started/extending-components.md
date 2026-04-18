# Extending Components

Every MonoBehaviour in the SDK is built to be subclassed. Lifecycle methods (`Awake`, `Start`, `Update`, etc.) are `protected virtual`, key state fields are `protected` where it makes sense, and hot customisation points (e.g. `NetworkNPC.SampleWander`) are `protected virtual` so you replace just the part you care about.

Three patterns cover most cases.

## Pattern 1 — Layer on top (most common)

Override a lifecycle method, call `base.X()` first, then add your own work. The base behaviour runs as normal; you tack logic on top.

```csharp
public class GlowingCube : NetworkObject
{
    public Light glow;
    Color _normal;

    protected override void Awake()
    {
        base.Awake();                     // NetworkObject still captures Rigidbody etc.
        _normal = glow.color;
    }

    void Update()
    {
        // React to claim state.
        glow.color = IsClaimedLocally  ? Color.cyan
                   : IsClaimedByRemote ? Color.yellow
                                       : _normal;
    }
}
```

Use when: you want the base component's behaviour + something extra. Works for any component.

## Pattern 2 — Replace a single hook

The component defines a `protected virtual` method for the customisable operation. Override only that method; the surrounding state machine keeps running.

```csharp
// Example: custom wander path for NetworkNPC. Claim/release handshake,
// chase logic, re-anchor-on-release — all unchanged.
public class ScriptedPathNPC : NetworkNPC
{
    public Transform[] path;

    protected override Vector3 SampleWander(double t, int seed, out Vector3 forward)
    {
        if (path.Length < 2) { forward = Vector3.zero; return _origin; }
        float segDur = 2f;
        int   i    = (int)(t / segDur) % path.Length;
        int   next = (i + 1) % path.Length;
        float s    = (float)((t % segDur) / segDur);

        forward = (path[next].position - path[i].position).normalized;
        return Vector3.Lerp(path[i].position, path[next].position, s);
    }
}
```

Use when: the component has an explicit hook for what you want to change. Check the component's page for a **Hooks** section or scan for `protected virtual`.

## Pattern 3 — Compose via the public API

Don't subclass; write a separate MonoBehaviour that references the SDK component.

```csharp
[RequireComponent(typeof(NetworkObject))]
public class DoorController : MonoBehaviour
{
    NetworkObject _obj;
    NetworkVariables _vars;
    NetworkEvents _events;

    void Awake()
    {
        _obj    = GetComponent<NetworkObject>();
        _vars   = GetComponent<NetworkVariables>();
        _events = GetComponent<NetworkEvents>();
    }

    public void Open()
    {
        // Any client can open a free-standing door (HasAuthority() is true
        // when nobody owns it). If someone's grabbing, only they can open.
        if (!_obj.HasAuthority()) return;
        _vars.Set("isOpen", true);
        _events.Send("open");
    }
}
```

Use when: you want to orchestrate multiple components, or the thing you're building isn't really *a kind of* NetworkObject / NetworkNPC, it just *uses* them.

## Cheat sheet: what's overridable

| Component | Key virtuals | Protected fields |
|---|---|---|
| `NetworkObject` | `Awake` | — (use the public claim/release API + `HasAuthority()`) |
| `NetworkManager` | `Awake`, `Start`, `Update`, `OnDestroy` | — |
| `NetworkNPC` | `Awake`, `Update`, `SampleWander`, `ApplyFacing` | `_netObj`, `_origin`, `_wanderEpoch`, `_anchoredReleaseMs`, `_nextClaimAttemptTime` |
| `NetworkVariables` | `Start`, `Update` | — |
| `NetworkEvents` | `Start` | — |
| `NetworkAnimator` | `Start`, `Update` | — |
| `NetworkTimer` | `Awake`, `Start`, `Update` | — |
| `NetworkParticle` | `Awake`, `Start`, `Update` | — |
| `NetworkWaypoint` | `Awake`, `Update` | — |
| `PlayerPushDetector` | `Awake` | — |

Static utilities (`NetworkPersistence`, `Protocol`) can't be subclassed — the public API is the extension surface.

## When `base.X()` matters

For lifecycle methods, **always call `base.X()`** unless you're intentionally replacing the parent's work. Skipping it breaks things in non-obvious ways:

```csharp
// BAD — no base.Awake()
public class BrokenObject : NetworkObject
{
    protected override void Awake() {
        // Rigidbody wasn't captured, cached sibling components weren't
        // fetched, _wasKinematicAtAwake isn't set — silent failures later.
    }
}

// GOOD
public class FineObject : NetworkObject
{
    protected override void Awake() {
        base.Awake();
        // your work here
    }
}
```

For hook methods (`SampleWander`, `ApplyFacing`, etc.), calling `base` is optional — they're meant to be fully replaced unless you want to add to the default behaviour.

## What about Unity's execution order?

Each component declares its own `[DefaultExecutionOrder]` internally. When you subclass, your override runs at that same order — Unity doesn't add a second reflection step. If you need *your* subclass to run earlier/later, add your own `[DefaultExecutionOrder(N)]` attribute; it overrides the base.

```csharp
[DefaultExecutionOrder(-200)]   // run before NetworkObject's -100
public class EarlyBirdNPC : NetworkNPC { ... }
```

## Testing your extension

1. **Add the subclass to a scene object.** Unity inspector shows all inherited properties plus any new `[SerializeField]`s you added.
2. **Run two clients.** Confirm your behaviour reaches both, and that the inherited networking still works (objects still sync, claims still work, NPCs still chase).
3. **If the custom thing is deterministic (e.g. `SampleWander`),** the easiest validation is to run a headless bot swarm against the server (`tools/bot_client.py`), which records CSVs from outside the Unity sandbox. `tools/netdbg_diff.py` joins any two and reports per-NPC position delta — expect < 0.5 m. For in-engine spot-checks, [NetworkDebug](../components/) prints the same state to the Unity console on each client so you can eyeball divergence.

## Related

- [**NetworkObject**](../components/network-object) — extending the core sync component
- [**NetworkNPC**](../components/network-npc) — has the most hooks; good worked examples
- [**NetworkManager**](../components/network-manager) — overriding the root

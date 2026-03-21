# NetworkAnimator

Syncs Unity Animator state, parameters, and triggers across clients. Attach alongside `NetworkObject` + `Animator`.

## Inspector Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `syncRate` | int | `10` | How often to check for state changes (Hz) |

## What It Syncs (Automatically)

- **Current animation state** — hash + normalized time
- **Parameters** — float, int, bool (change-detected, only sends when values change)
- **Triggers** — fire-and-forget via NetworkEvents

You don't need to manually send anything for parameters — `NetworkAnimator` detects changes automatically. The only exception is **triggers**, which must be sent through `NetworkAnimator.SetTrigger()`.

## Usage

```csharp
var netAnim = GetComponent<NetworkAnimator>();

// These sync automatically to all clients
netAnim.SetBool("walking", true);
netAnim.SetFloat("speed", 1.5f);
netAnim.SetInteger("weaponType", 2);
netAnim.SetTrigger("attack");  // Must use NetworkAnimator for triggers!
```

### Setting Parameters Directly on Animator

You can also set parameters directly on the Unity `Animator` component:

```csharp
GetComponent<Animator>().SetBool("walking", true);
```

`NetworkAnimator` will detect the change on the next sync tick and send it. **However, triggers set directly on the Animator will NOT be synced** — always use `netAnim.SetTrigger()` for triggers.

## Protocol

```
[0x07][u16 obj_id][i32 state_hash][f32 normalized_time][u8 dirty_count]
[per param: u8 name_len, name, u8 type, 4B value]
```

Parameter types: 0 = int, 1 = float, 2 = bool, 4 = trigger

## Example: Animated NPC

```csharp
public class NPCController : MonoBehaviour
{
    private NetworkAnimator _netAnim;
    private NetworkObject _netObj;

    void Start()
    {
        _netAnim = GetComponent<NetworkAnimator>();
        _netObj = GetComponent<NetworkObject>();
    }

    void Update()
    {
        // Only the authority drives the animator
        if (!_netObj.HasAuthority()) return;

        float speed = CalculateSpeed();
        _netAnim.SetFloat("speed", speed);
        _netAnim.SetBool("isGrounded", IsGrounded());

        if (ShouldAttack())
            _netAnim.SetTrigger("attack");
    }
}
```

## Tips

- Lower `syncRate` for objects with slow animations to save bandwidth
- Triggers are sent as events — if a client misses one, it won't replay
- State hash changes are always sent immediately (no rate limiting)

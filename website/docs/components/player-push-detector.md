# PlayerPushDetector

Tiny utility component that makes `NetworkObject.OnPlayerPush()` fire when the local player walks into something. You **do not need to add this manually** — NetworkManager attaches it on demand.

## When it's attached

| Condition | Result |
|---|---|
| `NetworkManager.autoAttachPushDetector = false` (default) | Not attached. Server's Rapier player capsule handles continuous push physics from your normal movement. |
| `NetworkManager.autoAttachPushDetector = true` | Attached at runtime to the resolved local player's body (`NonXR.Body` / `XR.Body`) as soon as `NetworkManager.LocalPlayer` resolves. |

The server's kinematic player-capsule integration in Rapier is the main push mechanism now — it pushes objects continuously based on the contacts it sees. PlayerPushDetector is an extra one-shot impulse emitted on first contact, for cases where you specifically want that extra "bump" feel (e.g. kick-the-ball moments).

## How it works

Unity's `CharacterController` doesn't participate in the normal physics collision pipeline — it uses its own sweep-based movement and fires `OnControllerColliderHit` on itself rather than `OnCollisionEnter` on the thing it hit.

PlayerPushDetector sits on the `CharacterController` and receives those callbacks. On a hit against a GameObject with a `NetworkObject`, it sends `MSG_PUSH_IMPULSE` to the relay. It does not claim the object.

## Inspector (if you attach it yourself)

The component has no inspector fields — all behaviour is controlled via the `NetworkObject.pushable` flag on whatever you hit.

## When to enable

```
NetworkManager.autoAttachPushDetector = true
```

- You want an instantaneous impulse felt on first contact, not just continuous push.
- You're building a kick-/bump-based game mechanic.
- You want PlayerPushDetector's logic even without the server-side capsule (e.g. running against a custom server).

Leave it off for default VR world behaviour — the server capsule already feels natural.

## Extending

Rarely needed. If you want custom contact handling:

```csharp
public class CustomPushDetector : PlayerPushDetector
{
    protected override void Awake()
    {
        base.Awake();
        // extra init
    }

    // There's no overridable "on-hit" yet — if you need finer control,
    // either ignore PlayerPushDetector entirely and write your own
    // component on the CharacterController, or file an issue and we'll
    // expose a hook.
}
```

For most gameplay extensions (e.g. "only trigger on fast hits") you'll want to detect on the object side — subclass NetworkObject and override `OnPlayerPush` to gate.

## Don't

- **Don't** attach this to a non-CharacterController GameObject. It only receives `OnControllerColliderHit`, nothing else.
- **Don't** enable `autoAttachPushDetector` on the server — the server doesn't have a CharacterController and there's nothing for the component to bind to.

## Related

- [**NetworkObject**](./network-object) — the `pushable` flag + the `OnPlayerPush()` entrypoint
- [**NetworkManager**](./network-manager) — where `autoAttachPushDetector` lives

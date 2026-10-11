# Example: Interactive Door

A door whose open/closed state is cached by the relay and replayed to late joiners.

## Components

```
Door
├── Animator (states: Closed, Open)
├── NetworkObject (entityType: "door", pushable: false, grabbable: false)
├── NetworkVariables
├── NetworkEvents
├── NetworkAnimator
└── Door.cs (below)
```

## Script

```csharp
using UnityEngine;
using SomniumSpace.Worlds.bently.networking; // For Somnium upload, use your creator namespace.

public class Door : MonoBehaviour
{
    private NetworkVariables _vars;
    private NetworkAnimator _netAnim;
    private NetworkObject _netObj;
    private NetworkEvents _events;

    void Start()
    {
        _vars = GetComponent<NetworkVariables>();
        _netAnim = GetComponent<NetworkAnimator>();
        _netObj = GetComponent<NetworkObject>();
        _events = GetComponent<NetworkEvents>();

        _vars.DefineVariable("isOpen", NetworkVariables.VarType.Bool, NetworkVariables.SyncMode.Immediate, persist: true);

        _vars.OnValueChanged += (name, type) =>
        {
            if (name == "isOpen")
                _netAnim.SetBool("open", _vars.GetBool("isOpen"));
        };
        _events.On("toggle_request", () =>
        {
            if (_netObj.HasAuthority())
                _vars.SetBool("isOpen", !_vars.GetBool("isOpen"));
        });
    }

    // Wire this to a trigger zone, button press, raycast hit, etc.
    public void ToggleDoor()
    {
        _events.SendToAuthority("toggle_request");
    }
}
```

## How It Works

1. Player interacts → `ToggleDoor()` fires
2. `NetworkVariables` syncs `isOpen` to all clients (Immediate mode = instant)
3. `OnValueChanged` triggers → sets Animator bool
4. `NetworkAnimator` syncs the animation state
5. Late joiners receive the cached `isOpen` value and apply it to the local Animator.

## Trigger Zone Version

A trigger can call `ToggleDoor()`. Debounce the trigger so the same player action sends one request; each request toggles once at the room writer.

```csharp
public class DoorTrigger : MonoBehaviour
{
    public Door door;
    private int _playersInZone = 0;

    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            _playersInZone++;
            if (_playersInZone == 1) door.ToggleDoor(); // Open
        }
    }

    void OnTriggerExit(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            _playersInZone--;
            if (_playersInZone == 0) door.ToggleDoor(); // Close
        }
    }
}
```

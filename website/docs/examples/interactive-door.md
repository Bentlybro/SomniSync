# Example: Interactive Door

A door any player can open/close. State persists for late joiners.

## Components

```
Door
├── Animator (states: Closed, Open)
├── NetworkObject (entityType: "door", pushable: false, grabbable: false)
├── NetworkVariables
├── NetworkAnimator
└── Door.cs (below)
```

## Script

```csharp
using UnityEngine;
using SomniumSpace.Worlds.bently.networking;

public class Door : MonoBehaviour
{
    private NetworkVariables _vars;
    private NetworkAnimator _netAnim;
    private NetworkObject _netObj;

    void Start()
    {
        _vars = GetComponent<NetworkVariables>();
        _netAnim = GetComponent<NetworkAnimator>();
        _netObj = GetComponent<NetworkObject>();

        _vars.DefineVariable("isOpen", VarType.Bool, SyncMode.Immediate, persist: true);

        _vars.OnValueChanged += (name, type) =>
        {
            if (name == "isOpen")
                _netAnim.SetBool("open", _vars.GetBool("isOpen"));
        };
    }

    // Wire this to a trigger zone, button press, raycast hit, etc.
    public void ToggleDoor()
    {
        if (!_netObj.HasAuthority()) return;
        bool current = _vars.GetBool("isOpen");
        _vars.SetBool("isOpen", !current);
    }
}
```

## How It Works

1. Player interacts → `ToggleDoor()` fires
2. `NetworkVariables` syncs `isOpen` to all clients (Immediate mode = instant)
3. `OnValueChanged` triggers → sets Animator bool
4. `NetworkAnimator` syncs the animation state
5. Late joiners receive `isOpen = true` via snapshot (persist: true)

## Trigger Zone Version

Want the door to open when players walk near it?

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

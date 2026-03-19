# Example: Interactive Door *(Planned — requires NetworkVariables)*

Sync a door that any player can open/close.

## Concept

```csharp
using UnityEngine;
using SomniumSpace.Worlds.bently.networking;

public class Door : MonoBehaviour
{
    private NetworkVariables _vars;
    private Animator _animator;

    void Start()
    {
        _vars = GetComponent<NetworkVariables>();
        _animator = GetComponent<Animator>();

        // React to changes from any client
        _vars.OnValueChanged += (name, type) =>
        {
            if (name == "isOpen")
                _animator.SetBool("open", _vars.GetBool("isOpen"));
        };
    }

    // Called by your interaction system (button press, trigger zone, etc.)
    public void ToggleDoor()
    {
        bool current = _vars.GetBool("isOpen");
        _vars.SetBool("isOpen", !current);
    }
}
```

## Setup

1. Door GameObject with `Animator` (open/close animations)
2. Add `NetworkObject` (entityType: `"door"`)
3. Add `NetworkVariables` — define a `bool` variable called `isOpen`
4. Add your `Door` script
5. Wire up `ToggleDoor()` to whatever triggers it

Any player can open the door. All other players see it sync.

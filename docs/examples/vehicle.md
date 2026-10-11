# Example: Networked Vehicle

A driveable vehicle that any player can enter/exit. Movement is driven by the claiming client while grabbed, then reconciled through server-authoritative physics snapshots.

## Components

```
Vehicle
├── Rigidbody (mass: 1500, drag: 0.5)
├── BoxCollider
├── NetworkObject (entityType: "vehicle", pushable: false, grabbable: true)
├── NetworkVariables                             ← "driver" (string), "engine" (bool)
├── NetworkEvents                                ← "enter", "exit", "honk"
└── Vehicle.cs
```

## Script

```csharp
public class Vehicle : MonoBehaviour
{
    private NetworkObject _netObj;
    private NetworkVariables _vars;
    private NetworkEvents _events;
    private Rigidbody _rb;

    public float speed = 20f;
    public float turnSpeed = 50f;
    public Transform driverSeat;

    void Start()
    {
        _netObj = GetComponent<NetworkObject>();
        _vars = GetComponent<NetworkVariables>();
        _events = GetComponent<NetworkEvents>();
        _rb = GetComponent<Rigidbody>();

        _vars.DefineVariable("driver", VarType.String, SyncMode.Immediate, persist: true);
        _vars.DefineVariable("engine", VarType.Bool, SyncMode.Immediate, persist: true);

        _events.On("enter", (string playerName) =>
        {
            _vars.SetString("driver", playerName);
            _vars.SetBool("engine", true);
        });

        _events.On("exit", () =>
        {
            _vars.SetString("driver", "");
            _vars.SetBool("engine", false);
        });

        _events.On("honk", () => AudioSource.PlayClipAtPoint(hornClip, transform.position));
    }

    // Called by player interaction system
    public void EnterVehicle(string playerName)
    {
        _netObj.Grab(); // Claim ownership
        _events.Send("enter", playerName);
    }

    public void ExitVehicle()
    {
        _events.Send("exit");
        _netObj.Release();
    }

    void FixedUpdate()
    {
        if (!_netObj.IsClaimedLocally) return;
        if (!_vars.GetBool("engine")) return;

        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");

        _rb.AddForce(transform.forward * v * speed);
        _rb.AddTorque(Vector3.up * h * turnSpeed);
    }
}
```

## How It Works

1. Player walks up to vehicle → `EnterVehicle()` claims ownership via `Grab()`
2. Held-object updates drive the server body while the driver owns the claim
3. `NetworkVariables` tracks who's driving (persists for late joiners)
4. Player exits → `Release()` returns vehicle to master authority
5. Vehicle coasts to a stop under server physics, then sleeps

## Why Claiming Matters Here

Only the claiming driver should write vehicle motion. Other clients receive the server's authoritative snapshots and interpolate toward them.

# NetworkEvents

Events are transient and can be sent by any room client. The relay validates their framing and caps each event at 4 KiB, names at 64 UTF-8 bytes, and parameters at 16 (string parameters at 1 KiB). It also limits each sender's event traffic to 30 events/s and 64 KiB/s sustained, with short bursts allowed. Use server-validated commands for health, currency, scores, or other trusted state.

Fire-and-forget networked events (RPCs). Send events from any client, all others receive them.

**Events are NOT persistent** — late joiners won't see past events. For persistent data, use [NetworkVariables](./network-variables.md).

## Inspector Properties

No Inspector properties — everything is configured via code.

## Listening for Events

```csharp
var events = GetComponent<NetworkEvents>();

// No parameters
events.On("explode", () => {
    Instantiate(explosionPrefab, transform.position, Quaternion.identity);
});

// Typed parameters
events.On("damage", (int amount) => TakeDamage(amount));
events.On("chat", (string message) => ShowMessage(message));
events.On("heal", (float amount) => RestoreHealth(amount));
events.On("toggle", (bool state) => SetActive(state));

// Multiple parameters (raw)
events.OnRaw("spawn", (object[] args) => {
    float x = (float)args[0];
    float y = (float)args[1];
    float z = (float)args[2];
    SpawnAt(new Vector3(x, y, z));
});

// Remove listeners
events.Off("explode");
```

## Sending Events

```csharp
events.Send("explode");                    // No params
events.Send("damage", 25);                 // int
events.Send("chat", "Hello!");             // string
events.Send("heal", 10.5f);               // float
events.Send("toggle", true);              // bool
events.Send("spawn", 1.0f, 2.0f, 3.0f);  // multiple params
```

**Events fire locally too** — the sender also receives the callback.

Use `events.SendToAuthority("request_name", args)` when any player may request a change to an object's state. The relay elects one authority for unclaimed objects and the current holder is authority for claimed objects. Register the handler on every client, then check `NetworkObject.HasAuthority()` before applying the request. The relay routes the event; your handler must validate game rules and input.

## Supported Parameter Types

| Type | Code | Size |
|------|------|------|
| `int` | 0 | 4 bytes |
| `float` | 1 | 4 bytes |
| `bool` | 2 | 1 byte |
| `string` | 3 | variable |

## Protocol

```
[0x06][u16 object_id][u8 name_len][name][u8 param_count][per param: u8 type, u16 len, value]
```

## Example: Exploding Barrel

```csharp
public class ExplodingBarrel : MonoBehaviour
{
    private NetworkEvents _events;
    public GameObject explosionVFX;
    public float radius = 5f;

    void Start()
    {
        _events = GetComponent<NetworkEvents>();
        _events.On("explode", (float r) => {
            Instantiate(explosionVFX, transform.position, Quaternion.identity);
            foreach (var col in Physics.OverlapSphere(transform.position, r))
            {
                var rb = col.GetComponent<Rigidbody>();
                if (rb != null)
                    rb.AddExplosionForce(500f, transform.position, r);
            }
        });
    }

    public void Detonate()
    {
        _events.Send("explode", radius);
    }
}
```

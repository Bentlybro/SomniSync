# NetworkVariables

Sync custom data (int, float, bool, string) across all clients. Add alongside `NetworkObject`.

## Inspector Properties

Define variables visually — name, type, default value, sync mode, and persistence.

| Field | Description |
|-------|-------------|
| `name` | Variable identifier (used in Get/Set calls) |
| `type` | `Int`, `Float`, `Bool`, or `String` |
| `defaultValue` | Starting value (as string, parsed on init) |
| `syncMode` | `OnChange` (batched, default) or `Immediate` (instant) |
| `persistForLateJoiners` | Cache the latest value on the relay and replay it to late joiners (default: on) |

## Code API

```csharp
var vars = GetComponent<NetworkVariables>();

// --- Get values ---
int health = vars.GetInt("health");
float speed = vars.GetFloat("speed");
bool open = vars.GetBool("doorOpen");
string name = vars.GetString("ownerName");

// --- Set values (auto-syncs to all clients) ---
vars.SetInt("health", 75);
vars.SetFloat("speed", 3.5f);
vars.SetBool("doorOpen", true);
vars.SetString("ownerName", "Bently");

// --- React to changes (from any client) ---
vars.OnValueChanged += (string varName, NetworkVariables.VarType type) =>
{
    if (varName == "health")
        healthBar.value = vars.GetInt("health");
};

// --- Check if variable exists ---
if (vars.HasVariable("score")) { ... }

// --- Define variables at runtime (alternative to Inspector) ---
vars.DefineVariable("score", NetworkVariables.VarType.Int,
    syncMode: NetworkVariables.SyncMode.Immediate,
    persist: true);
```

## Authority and conflicts

The relay elects one room state writer and announces it with `CTRL_STATE_WRITER`. Only that client may write variables and animator state on an **unclaimed** object. A claimed object's holder writes its state until release. Other clients can send `NetworkEvents` to the authority. The writer moves to another connected client if it disconnects. This prevents duplicate timers and accidental concurrent writes, but the relay does not validate game rules such as who may change health or score.

## Sync Modes

| Mode | When It Sends | Use Case |
|------|---------------|----------|
| `OnChange` | Batched with the next tick | Most variables (health, scores, toggles) |
| `Immediate` | Instantly when set | Game-critical events (game over, round start) |

## Late joiners

The relay caches the latest transmitted value of each variable with `persistForLateJoiners` enabled and sends it in `0x12` snapshot chunks when a client joins. This cache is in memory, limited to 16,384 values and 2 MiB per room, and disappears when the room empties or the relay restarts. Variables marked transient are not replayed. This is separate from account [persistence](network-persistence.md).

## Protocol

Message type `0x04`:
```
[0x04][u16 object_id][u8 var_count][per var: u8 name_len, name, u8 type_flags, u16 value_len, value]
```

The low bits of `type_flags` are `0` = Int, `1` = Float, `2` = Bool, `3` = String. Bit 7 requests late-join replay. Names are 1–64 UTF-8 bytes; values are at most 4096 bytes. The relay rejects malformed updates. `0x12` snapshots contain `[u16 count]` followed by `[u16 object_id]` and one variable record for each cached value.

## Example: Door

```csharp
public class Door : MonoBehaviour
{
    private NetworkVariables _vars;
    private Animator _animator;

    void Start()
    {
        _vars = GetComponent<NetworkVariables>();
        _animator = GetComponent<Animator>();

        _vars.OnValueChanged += (name, type) =>
        {
            if (name == "isOpen")
                _animator.SetBool("open", _vars.GetBool("isOpen"));
        };
    }

    public void ToggleDoor()
    {
        _vars.SetBool("isOpen", !_vars.GetBool("isOpen"));
    }
}
```

## Example: Health System

```csharp
public class Enemy : MonoBehaviour
{
    private NetworkVariables _vars;

    void Start()
    {
        _vars = GetComponent<NetworkVariables>();
        _vars.DefineVariable("health", NetworkVariables.VarType.Int,
            syncMode: NetworkVariables.SyncMode.Immediate);
        _vars.SetInt("health", 100);

        _vars.OnValueChanged += (name, type) =>
        {
            if (name == "health" && _vars.GetInt("health") <= 0)
                Die();
        };
    }

    public void TakeDamage(int amount)
    {
        int hp = _vars.GetInt("health") - amount;
        _vars.SetInt("health", Mathf.Max(0, hp));
    }
}
```

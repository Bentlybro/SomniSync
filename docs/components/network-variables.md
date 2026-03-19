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
| `persistForLateJoiners` | Include in snapshot so late joiners get current state |

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

## Authority

Variables can only be set by the client that **owns** the object:
- **Master** can set variables on any unowned object
- **Spectators** can set variables on objects they've **claimed** (via interaction)
- This prevents conflicts — only one client writes at a time

## Sync Modes

| Mode | When It Sends | Use Case |
|------|---------------|----------|
| `OnChange` | Batched with the next tick | Most variables (health, scores, toggles) |
| `Immediate` | Instantly when set | Game-critical events (game over, round start) |

## Late Joiner Sync

Variables with `persistForLateJoiners = true` (default) are included in the snapshot sent to new clients. They'll see the current state, not defaults.

Set `persistForLateJoiners = false` for ephemeral data (e.g., "player is typing" indicators).

## Protocol

Message type `0x04`:
```
[0x04][u16 object_id][u8 var_count][per var: u8 name_len, name, u8 type, u16 value_len, value]
```

Type bytes: `0` = Int, `1` = Float, `2` = Bool, `3` = String

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

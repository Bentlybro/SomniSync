# NetworkParticle

Syncs ParticleSystem play/stop and optionally properties (emission rate, color, speed, size) across clients.

Built on `NetworkEvents` + `NetworkVariables` — no custom protocol needed. Automatically adds those components if missing.

## Inspector Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `syncPlayState` | bool | `true` | Sync play/stop state |
| `syncEmissionRate` | bool | `false` | Sync emission rate over time |
| `syncStartColor` | bool | `false` | Sync start color |
| `syncStartSpeed` | bool | `false` | Sync start speed |
| `syncStartSize` | bool | `false` | Sync start size |
| `syncRate` | float | `5` | Property check frequency (Hz) |

## Usage

```csharp
var particles = GetComponent<NetworkParticle>();

// Play/stop — synced to all clients
particles.Play();
particles.Stop();
```

Properties are synced automatically when they change on the ParticleSystem component. Just modify the ParticleSystem normally.

## Example: Networked Torch

```csharp
public class NetworkedTorch : MonoBehaviour
{
    private NetworkParticle _netParticle;
    private NetworkVariables _vars;

    void Start()
    {
        _netParticle = GetComponent<NetworkParticle>();
        _vars = GetComponent<NetworkVariables>();
        _vars.DefineVariable("lit", VarType.Bool, SyncMode.Immediate, persist: true);
        _vars.OnValueChanged += OnChanged;
    }

    public void ToggleFlame()
    {
        bool lit = !_vars.GetBool("lit");
        _vars.SetBool("lit", lit);
        if (lit) _netParticle.Play();
        else _netParticle.Stop();
    }

    private void OnChanged(string name, VarType type)
    {
        if (name == "lit")
        {
            if (_vars.GetBool("lit")) _netParticle.Play();
            else _netParticle.Stop();
        }
    }
}
```

## Tips

- For simple play/stop only, leave all property syncs off (lightweight)
- Property sync uses NetworkVariables — persistent for late joiners
- Play/stop uses NetworkEvents — NOT persistent (late joiners won't see the trigger, but the particle state is driven by variables)

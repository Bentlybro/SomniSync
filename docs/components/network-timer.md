# NetworkTimer

Authority-driven networked countdown timer. Syncs to all clients via NetworkVariables. Survives master transfers.

## Inspector Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `duration` | float | `60` | Duration in seconds |
| `loop` | bool | `false` | Restart when finished |
| `autoStart` | bool | `false` | Start automatically on scene load (authority only) |
| `syncRate` | float | `2` | How often to sync remaining time (Hz) |

## Events

| Event | Type | Description |
|-------|------|-------------|
| `OnStarted` | `UnityEvent` | Fires when timer starts |
| `OnFinished` | `UnityEvent` | Fires when timer reaches zero |
| `OnTick` | `UnityEvent<float>` | Fires every frame with remaining seconds |

Wire these up in the Inspector or via code.

## Usage

```csharp
var timer = GetComponent<NetworkTimer>();

// Start a countdown (authority only)
timer.StartTimer(60f);

// Control
timer.StopTimer();
timer.ResetTimer();

// Read state (any client)
float remaining = timer.Remaining;    // seconds left
bool running = timer.IsRunning;       // is it counting down?
float progress = timer.Progress;      // 0.0 (start) → 1.0 (done)
```

## How It Works

1. Authority starts the timer → syncs duration + running state via NetworkVariables
2. Authority counts down locally, syncs remaining time at `syncRate`
3. Spectators receive remaining time updates, tick down locally between syncs (smooth display)
4. When timer hits zero → `OnFinished` fires on all clients
5. If `loop = true` → restarts automatically

**Survives master transfer:** If the master disconnects, the new master picks up from the last synced value. No timer reset.

## Example: Timed Bomb

```csharp
public class TimedBomb : MonoBehaviour
{
    private NetworkTimer _timer;
    private NetworkEvents _events;
    public TextMesh timerText;
    public GameObject explosionPrefab;

    void Start()
    {
        _timer = GetComponent<NetworkTimer>();
        _events = GetComponent<NetworkEvents>();

        _timer.OnTick.AddListener(remaining => {
            timerText.text = remaining.ToString("F1");
        });

        _timer.OnFinished.AddListener(() => {
            _events.Send("explode");
        });

        _events.On("explode", () => {
            Instantiate(explosionPrefab, transform.position, Quaternion.identity);
            gameObject.SetActive(false);
        });
    }

    public void Activate()
    {
        _timer.StartTimer(10f); // 10 second fuse
    }
}
```

## Example: Round Timer

```csharp
public class RoundManager : MonoBehaviour
{
    private NetworkTimer _timer;
    private NetworkVariables _vars;

    void Start()
    {
        _timer = GetComponent<NetworkTimer>();
        _vars = GetComponent<NetworkVariables>();
        _vars.DefineVariable("round", VarType.Int, SyncMode.Immediate, persist: true);

        _timer.OnFinished.AddListener(() => {
            int round = _vars.GetInt("round") + 1;
            _vars.SetInt("round", round);
            _timer.StartTimer(120f); // 2 minute rounds
        });
    }

    public void StartGame()
    {
        _vars.SetInt("round", 1);
        _timer.StartTimer(120f);
    }
}
```

## Tips

- Low `syncRate` (1-2 Hz) is usually enough — spectators interpolate locally
- `OnTick` fires every frame, not at sync rate — use it for smooth UI
- `OnFinished` fires on ALL clients, not just authority
- Combine with NetworkEvents for actions that happen when the timer ends

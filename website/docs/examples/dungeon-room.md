# Example: Dungeon Room

A combat/puzzle room with NPCs, traps, loot, and a boss timer. Uses most components together.

## Scene Layout

```
DungeonRoom
├── [NetworkManager] (roomId: "dungeon-floor-1")
│
├── --- NPCs ---
│   └── Skeleton_001..005
│       ├── Animator
│       ├── Rigidbody (isKinematic: true)
│       ├── NetworkObject (entityType: "skeleton", pushable: false, grabbable: false)
│       ├── NetworkAnimator (syncRate: 10)
│       ├── NetworkVariables                     ← health, alive state
│       ├── NetworkEvents                        ← "damage", "die" events
│       └── SkeletonAI.cs
│
├── --- Traps ---
│   └── SpikeTrap_001..003
│       ├── Animator
│       ├── NetworkObject (entityType: "trap", pushable: false, grabbable: false)
│       ├── NetworkAnimator
│       ├── NetworkTimer (duration: 3, loop: true, autoStart: true)
│       └── SpikeTrap.cs
│
├── --- Torches ---
│   └── Torch_001..008
│       ├── ParticleSystem
│       ├── NetworkObject (entityType: "torch", pushable: false, grabbable: false)
│       ├── NetworkVariables                     ← "lit" bool
│       ├── NetworkParticle (syncPlayState: true)
│       └── NetworkedTorch.cs
│
├── --- Loot ---
│   └── Chest_001
│       ├── Animator
│       ├── NetworkObject (entityType: "chest", pushable: false, grabbable: false)
│       ├── NetworkVariables                     ← "opened" bool
│       ├── NetworkAnimator
│       └── LootChest.cs
│
├── --- Physics Props ---
│   └── Barrel_001..010
│       ├── Rigidbody
│       ├── NetworkObject (entityType: "barrel")
│       ├── NetworkEvents                        ← "explode" event
│       ├── NetworkParticle                      ← explosion VFX
│       └── ExplodingBarrel.cs
│
└── --- Boss Timer ---
    └── BossTimer
        ├── NetworkObject (entityType: "boss_timer", pushable: false, grabbable: false)
        ├── NetworkTimer (duration: 300)         ← 5 minute countdown
        ├── NetworkEvents                        ← "boss_spawn" event
        └── BossSpawner.cs
```

## Scripts

### Skeleton NPC

```csharp
public class SkeletonAI : MonoBehaviour
{
    private NetworkObject _netObj;
    private NetworkVariables _vars;
    private NetworkEvents _events;
    private NetworkAnimator _netAnim;

    void Start()
    {
        _netObj = GetComponent<NetworkObject>();
        _vars = GetComponent<NetworkVariables>();
        _events = GetComponent<NetworkEvents>();
        _netAnim = GetComponent<NetworkAnimator>();

        _vars.DefineVariable("health", VarType.Float, SyncMode.Immediate, persist: true);
        _vars.DefineVariable("alive", VarType.Bool, SyncMode.Immediate, persist: true);

        // Only master sets initial values
        if (NetworkManager.Instance.IsMaster)
        {
            _vars.SetFloat("health", 100f);
            _vars.SetBool("alive", true);
        }

        _events.On("damage", (float amount) =>
        {
            if (!_netObj.HasAuthority()) return;
            float hp = _vars.GetFloat("health") - amount;
            _vars.SetFloat("health", Mathf.Max(0, hp));
            _netAnim.SetTrigger("hit");

            if (hp <= 0)
            {
                _vars.SetBool("alive", false);
                _netAnim.SetTrigger("die");
            }
        });
    }

    void Update()
    {
        if (!_netObj.HasAuthority()) return;
        if (!_vars.GetBool("alive")) return;

        // Your AI logic here — patrol, chase, attack
        float speed = IsChasing() ? 2f : 0.5f;
        _netAnim.SetFloat("speed", speed);
    }

    // Called by player's attack system
    public void TakeDamage(float amount)
    {
        _events.Send("damage", amount);
    }
}
```

### Spike Trap (Timer-Driven)

```csharp
public class SpikeTrap : MonoBehaviour
{
    private NetworkTimer _timer;
    private NetworkAnimator _netAnim;

    void Start()
    {
        _timer = GetComponent<NetworkTimer>();
        _netAnim = GetComponent<NetworkAnimator>();

        _timer.OnFinished.AddListener(() =>
        {
            _netAnim.SetTrigger("activate");
            // Damage logic here
        });
    }
}
```

### Boss Spawner

```csharp
public class BossSpawner : MonoBehaviour
{
    private NetworkTimer _timer;
    private NetworkEvents _events;

    void Start()
    {
        _timer = GetComponent<NetworkTimer>();
        _events = GetComponent<NetworkEvents>();

        _timer.OnTick.AddListener(remaining =>
        {
            // Update a world-visible countdown display
            UpdateCountdownUI(remaining);
        });

        _timer.OnFinished.AddListener(() =>
        {
            _events.Send("boss_spawn");
        });

        _events.On("boss_spawn", () =>
        {
            // Dramatic entrance — particles, screen shake, etc.
            SpawnBoss();
        });
    }

    public void StartEncounter()
    {
        _timer.StartTimer(300f); // 5 minutes
    }
}
```

## How It All Connects

1. Players enter the dungeon room
2. Spike traps cycle on a 3-second `NetworkTimer` loop — all clients see them in sync
3. Players fight skeletons — damage via `NetworkEvents`, health via `NetworkVariables`, animations via `NetworkAnimator`
4. Push exploding barrels into skeletons — physics (chain claim) + explosion event + particle sync
5. Light/extinguish torches — `NetworkVariables` persists state, `NetworkParticle` syncs flames
6. Open the chest — `NetworkVariables` tracks opened state, `NetworkAnimator` plays open animation
7. Boss timer counts down — visible to all, spawns boss when done
8. Late joiners see: current torch states, skeleton health, chest opened/closed, timer remaining

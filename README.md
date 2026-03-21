# BentlyNet

High-performance real-time physics networking for Unity. Designed for VR worlds with thousands of objects. Relay-based architecture with binary protocol — tested with **4000+ objects** at 14 bytes per entity.

Built for [Somnium Space](https://somniumspace.com) VR but works with any Unity project using `System.Net.WebSockets`.

## Quick Start

1. Add `NetworkManager` to a GameObject in your scene
2. Set the `Server URL` (e.g. `wss://simple-sync.bentlybro.com`)
3. Set a `Room ID` (e.g. `my-world`)
4. Add `NetworkObject` to every object you want synced
5. Done.

That's it. Physics syncs automatically. Players can interact with objects by walking into them.

---

## Architecture

```
Master Client (first to join — runs physics for all objects)
    ↓ binary position updates (14 bytes/entity, half-precision)
Relay Server (Rust, zero-copy forwarding)
    ↓ broadcasts to all other clients
Spectator Clients (receive + apply positions, can interact)
    ↓ interaction updates for objects they grab/push
```

- **First client** = master (runs physics, sends position updates)
- **Everyone else** = spectators (receive updates, can interact with objects)
- **Master disconnects** → next client auto-promoted
- **Server** = dumb relay. No game logic. Just pipes bytes.

### Multi-Room Support

Each room is completely isolated — own master, own objects, own state.

```
ws://server/room-name        → joins/creates "room-name"
ws://server/                  → joins "default" room
wss://server/world-public     → separate from wss://server/world-private
```

Rooms are created on first connect and destroyed when the last client leaves.

---

## Components

### NetworkManager

**One per scene.** Handles connection, protocol, master/spectator logic, and object registration.

| Inspector Field | Default | Description |
|----------------|---------|-------------|
| `serverUrl` | `wss://simple-sync.bentlybro.com` | WebSocket server URL |
| `roomId` | *(empty)* | Room ID — clients with the same ID share a world |
| `reconnectDelay` | `3` | Seconds between reconnect attempts |
| `sendRate` | `60` | Position updates per second |
| `positionThreshold` | `0.01` | Minimum movement (meters) to include in update |
| `rotationThreshold` | `1` | Minimum rotation (degrees) to include in update |
| `resyncInterval` | `5` | Seconds between full snapshots (drift correction) |

```csharp
// Access anywhere via singleton
NetworkManager.Instance.IsConnected  // bool
NetworkManager.Instance.IsMaster     // bool
NetworkManager.Instance.ClientId     // ushort
```

---

### NetworkObject

**One per synced object.** Handles position/rotation sync, ownership, and physics toggling.

| Inspector Field | Default | Description |
|----------------|---------|-------------|
| `entityType` | `"object"` | Type string for deterministic sorting (e.g. `"cube"`, `"ball"`) |
| `smoothing` | `Medium` | Interpolation mode: `None`, `Low`, `Medium`, `High` |

#### How Ownership Works

```
Master → owns all objects by default (runs physics)
Spectator walks into object → auto-claims it (local physics)
Object settles (velocity ≈ 0) → auto-releases back to master
```

Claim/release is automatic via `PlayerPushDetector` (attached at runtime). Zero setup needed.

#### Code Examples

```csharp
// Check who controls this object
netObj.HasAuthority()       // true if this client controls it
netObj.IsClaimedLocally     // true if this client claimed it via interaction
netObj.IsClaimedByRemote    // true if another spectator claimed it

// Manual grab/release (for custom interaction systems)
netObj.Grab();    // Claim ownership, enable local physics
netObj.Release(); // Release back to master

// Smoothing modes
netObj.smoothing = NetworkObject.SmoothingMode.High; // More latency, smoother
netObj.smoothing = NetworkObject.SmoothingMode.None; // Snap to position (no interpolation)
```

#### Chain Claiming

When a claimed object collides with an unclaimed object, the unclaimed object is automatically claimed too. This creates a domino effect — push one object into a stack and the whole stack becomes yours temporarily.

---

### NetworkVariables

Sync custom data (int, float, bool, string) per object. Attach alongside `NetworkObject`.

| Inspector Field | Default | Description |
|----------------|---------|-------------|
| `variables` | *(list)* | Define variables in Inspector or via code |

#### Defining Variables

```csharp
var vars = GetComponent<NetworkVariables>();

// Define in Start() — choose sync mode per variable
vars.DefineVariable("health", VarType.Float, SyncMode.OnChange, persist: true);
vars.DefineVariable("isOpen", VarType.Bool, SyncMode.Immediate, persist: true);
vars.DefineVariable("playerName", VarType.String, SyncMode.OnChange, persist: true);
vars.DefineVariable("score", VarType.Int, SyncMode.OnChange, persist: true);
```

**Sync modes:**
- `OnChange` — batched with the tick. Bandwidth-friendly. Default.
- `Immediate` — sent the instant you call `Set`. Use for critical state changes.

**Persist:** If `true`, late joiners receive the current value via snapshot.

#### Getting and Setting

```csharp
// Set values (only works if you have authority over the object)
vars.SetFloat("health", 75.5f);
vars.SetBool("isOpen", true);
vars.SetString("playerName", "Alice");
vars.SetInt("score", 42);

// Get values (works on any client)
float hp = vars.GetFloat("health");
bool open = vars.GetBool("isOpen");
string name = vars.GetString("playerName");
int score = vars.GetInt("score");

// Check if variable exists
if (vars.HasVariable("health")) { ... }
```

#### Listening for Changes

```csharp
vars.OnValueChanged += (string name, VarType type) =>
{
    switch (name)
    {
        case "health":
            UpdateHealthBar(vars.GetFloat("health"));
            break;
        case "isOpen":
            door.SetActive(vars.GetBool("isOpen"));
            break;
    }
};
```

#### Example: Networked Door

```csharp
public class NetworkedDoor : MonoBehaviour
{
    private NetworkVariables _vars;
    private Animator _animator;

    void Start()
    {
        _vars = GetComponent<NetworkVariables>();
        _animator = GetComponent<Animator>();
        
        _vars.DefineVariable("isOpen", VarType.Bool, SyncMode.Immediate, persist: true);
        _vars.OnValueChanged += OnChanged;
    }

    // Call this when the player interacts with the door
    public void Toggle()
    {
        bool isOpen = _vars.GetBool("isOpen");
        _vars.SetBool("isOpen", !isOpen);
    }

    private void OnChanged(string name, VarType type)
    {
        if (name == "isOpen")
            _animator.SetBool("Open", _vars.GetBool("isOpen"));
    }
}
```

---

### NetworkEvents

Fire-and-forget RPCs. Send events from any client, all others receive them. Events are **not persistent** — late joiners won't see past events.

#### Listening for Events

```csharp
var events = GetComponent<NetworkEvents>();

// No parameters
events.On("explode", () =>
{
    Instantiate(explosionPrefab, transform.position, Quaternion.identity);
});

// With parameters
events.On("damage", (int amount) => TakeDamage(amount));
events.On("chat", (string message) => ShowChat(message));
events.On("hit", (float force) => ApplyKnockback(force));
events.On("toggle", (bool state) => SetActive(state));

// Raw params (multiple parameters)
events.OnRaw("spawn", (object[] args) =>
{
    float x = (float)args[0];
    float y = (float)args[1];
    float z = (float)args[2];
    SpawnAt(new Vector3(x, y, z));
});

// Remove listeners
events.Off("explode");
```

#### Sending Events

```csharp
// No parameters
events.Send("explode");

// With parameters
events.Send("damage", 25);
events.Send("chat", "Hello world!");
events.Send("hit", 5.5f);
events.Send("toggle", true);

// Multiple parameters
events.Send("spawn", 1.0f, 2.0f, 3.0f);
```

Events fire locally too — the sender also receives the callback.

#### Example: Exploding Barrel

```csharp
public class ExplodingBarrel : MonoBehaviour
{
    private NetworkEvents _events;
    public GameObject explosionVFX;
    public float explosionRadius = 5f;

    void Start()
    {
        _events = GetComponent<NetworkEvents>();
        _events.On("explode", (float radius) =>
        {
            Instantiate(explosionVFX, transform.position, Quaternion.identity);
            // Apply force to nearby rigidbodies
            foreach (var col in Physics.OverlapSphere(transform.position, radius))
            {
                var rb = col.GetComponent<Rigidbody>();
                if (rb != null)
                    rb.AddExplosionForce(500f, transform.position, radius);
            }
        });
    }

    public void Detonate()
    {
        _events.Send("explode", explosionRadius);
    }
}
```

---

### NetworkAnimator

Syncs Unity `Animator` state, parameters, and triggers across clients. Attach alongside `NetworkObject` + `Animator`.

| Inspector Field | Default | Description |
|----------------|---------|-------------|
| `syncRate` | `10` | How often to check for state changes (Hz) |

**Automatically syncs:**
- Current animation state hash + normalized time
- All Animator parameters (float, int, bool) — change-detected
- Triggers (via `SetTrigger()`)

#### Usage

```csharp
var netAnim = GetComponent<NetworkAnimator>();

// Set parameters — automatically synced to all clients
netAnim.SetBool("walking", true);
netAnim.SetFloat("speed", 1.5f);
netAnim.SetInteger("weaponType", 2);
netAnim.SetTrigger("attack");
```

You can also set parameters directly on the `Animator` — `NetworkAnimator` detects changes automatically. Using the `NetworkAnimator` methods is only required for triggers.

#### Example: Animated NPC

```csharp
public class NPCController : MonoBehaviour
{
    private NetworkAnimator _netAnim;
    private NetworkObject _netObj;

    void Start()
    {
        _netAnim = GetComponent<NetworkAnimator>();
        _netObj = GetComponent<NetworkObject>();
    }

    void Update()
    {
        if (!_netObj.HasAuthority()) return;

        // Only the authority drives the animator
        float speed = CalculateSpeed();
        _netAnim.SetFloat("speed", speed);
        _netAnim.SetBool("isGrounded", IsGrounded());

        if (ShouldAttack())
            _netAnim.SetTrigger("attack");
    }
}
```

---

### NetworkRigidbody

Syncs Rigidbody velocity and angular velocity using half-precision floats. **15 bytes per update.** Great for thrown objects, vehicles, physics puzzles, explosions.

| Inspector Field | Default | Description |
|----------------|---------|-------------|
| `syncRate` | `10` | How often to check for velocity changes (Hz) |
| `velocityThreshold` | `0.05` | Minimum velocity change to trigger sync (m/s) |
| `angularVelocityThreshold` | `0.1` | Minimum angular velocity change (rad/s) |
| `applyReceivedVelocity` | `true` | Apply received velocity for extrapolation |

Attach alongside `NetworkObject` + `Rigidbody`. No code needed — works automatically.

When the authority detects velocity changes, it sends updates. Receivers apply velocity so objects coast naturally between position updates (no stuttering on thrown objects).

#### Example: Throwable Object

```csharp
// Just add these components — no code needed:
// - NetworkObject
// - NetworkRigidbody
// - Rigidbody

// When a player grabs and throws this object:
// 1. NetworkObject.Grab() claims ownership
// 2. Player applies force (standard Unity physics)
// 3. NetworkRigidbody syncs the velocity to all clients
// 4. Other clients see the object flying with correct velocity
// 5. Object settles → auto-releases back to master
```

---

### NetworkParticle

Syncs ParticleSystem play/stop and optionally properties (emission rate, color, speed, size). Built on `NetworkEvents` + `NetworkVariables` — no custom protocol needed.

| Inspector Field | Default | Description |
|----------------|---------|-------------|
| `syncPlayState` | `true` | Sync play/stop |
| `syncEmissionRate` | `false` | Sync emission rate over time |
| `syncStartColor` | `false` | Sync start color |
| `syncStartSpeed` | `false` | Sync start speed |
| `syncStartSize` | `false` | Sync start size |
| `syncRate` | `5` | Property check frequency (Hz) |

#### Usage

```csharp
var particles = GetComponent<NetworkParticle>();

// Play/stop — synced to all clients
particles.Play();
particles.Stop();

// Properties are synced automatically when changed via the ParticleSystem component
// Just modify the ParticleSystem normally and NetworkParticle handles the rest
```

#### Example: Torch with Networked Flame

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

---

### NetworkTimer

Authority-driven countdown timer. Syncs to all clients via `NetworkVariables`. Survives master transfers.

| Inspector Field | Default | Description |
|----------------|---------|-------------|
| `duration` | `60` | Duration in seconds |
| `loop` | `false` | Restart when finished |
| `autoStart` | `false` | Start automatically on scene load |
| `syncRate` | `2` | How often to sync remaining time (Hz) |

**Events:** `OnStarted`, `OnFinished`, `OnTick(float remaining)`

#### Usage

```csharp
var timer = GetComponent<NetworkTimer>();

// Start a 60-second countdown
timer.StartTimer(60f);

// Control
timer.StopTimer();
timer.ResetTimer();

// Read state
float remaining = timer.Remaining;     // seconds left
bool running = timer.IsRunning;        // is it counting down?
float progress = timer.Progress;       // 0.0 (start) → 1.0 (done)
```

#### Example: Bomb Timer

```csharp
public class TimedBomb : MonoBehaviour
{
    private NetworkTimer _timer;
    private NetworkEvents _events;

    void Start()
    {
        _timer = GetComponent<NetworkTimer>();
        _events = GetComponent<NetworkEvents>();

        _timer.OnTick.AddListener(remaining =>
        {
            // Update UI countdown display
            timerText.text = remaining.ToString("F1");
        });

        _timer.OnFinished.AddListener(() =>
        {
            _events.Send("explode");
        });

        _events.On("explode", () =>
        {
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

---

### PlayerPushDetector

**Auto-attached at runtime** to the local player's CharacterController. Detects collisions between the player and NetworkObjects, triggering auto-claim.

No setup needed. No code needed. It just works.

### NetworkStats

F3 debug overlay showing FPS, connection status, and bandwidth.

```
FPS: 60
Status: MASTER (client_1)
Send: 120 msg/s (42.5 KB/s)
Recv: 5 msg/s (1.2 KB/s)
```

Add to any GameObject in your scene. Toggle visibility with F3.

### PlayerDebugMarker

Scene-view-only capsule visualization showing remote player positions. Invisible in game view. Useful for debugging.

---

## Protocol Reference

All data uses **half-precision floats** (16-bit) and **u16 rotation** encoding. 14 bytes per entity.

| Type | Name | Direction | Size/Entity |
|------|------|-----------|-------------|
| `0x01` | Position Update | master → others | 14 bytes |
| `0x02` | Snapshot | master → others | 14 bytes |
| `0x03` | Interaction | any → others | 14 bytes |
| `0x04` | Variable Update | any → others | variable |
| `0x06` | Event (RPC) | any → others | variable |
| `0x07` | Animator State | any → others | variable |
| `0x08` | Rigidbody | any → others | 15 bytes |
| `0xFF` | Control | server ↔ client | variable |

### Position Format (0x01, 0x02, 0x03)
```
[msg_type][u16 count][N × (u16 id + 6B half-pos + 6B u16-rot)]
```

### Bandwidth
- 2000 objects at 60Hz ≈ 28KB/tick ≈ 1.6MB/s
- 3000 objects confirmed smooth
- 4000 objects working (breakdown point)
- Photon limit: ~100 objects

---

## Server

Modular Rust server using `tokio` + `tokio-tungstenite`. Zero-copy message forwarding.

```
server/src/
├── main.rs       — entry point, startup, graceful shutdown
├── protocol.rs   — message constants
├── room.rs       — room state, ownership, multi-room map
├── client.rs     — client struct, broadcast helpers
├── handler.rs    — connection handler, message dispatch
└── config.rs     — server config
```

### Build & Run

```bash
cd server
cargo build --release

# Windows
.\target\release\somnium-relay.exe                  # default: ws://0.0.0.0:9090
.\target\release\somnium-relay.exe 0.0.0.0:8080     # custom port

# Linux/macOS
./target/release/somnium-relay
./target/release/somnium-relay 0.0.0.0:8080
```

Set `RUST_LOG=debug` for claim/release logging.

---

## Client File Structure

```
client/Unity/
├── Core/
│   ├── NetworkManager.cs     — connection, master/spectator, object registration
│   ├── NetworkObject.cs      — per-object sync, ownership, interaction
│   └── Protocol.cs           — binary encoding (half-float, u16 rotation)
├── Components/
│   ├── NetworkVariables.cs   — custom variable sync (int/float/bool/string)
│   ├── NetworkEvents.cs      — fire-and-forget RPCs
│   ├── NetworkAnimator.cs    — Animator state/param/trigger sync
│   ├── NetworkRigidbody.cs   — velocity + angular velocity sync
│   ├── NetworkParticle.cs    — ParticleSystem sync
│   ├── NetworkTimer.cs       — networked countdowns
│   └── PlayerPushDetector.cs — auto-attached collision detection
└── Debug/
    ├── NetworkStats.cs       — F3 overlay
    └── PlayerDebugMarker.cs  — scene-view player visualization
```

### Namespace

```csharp
using SomniumSpace.Worlds.bently.networking;
```

---

## Performance

| Objects | Status |
|---------|--------|
| 4000 | Working (breakdown point) |
| 3000 | Smooth |
| 2000 | Stable |
| 800 | Production-ready |
| 100 | Photon's limit |

- **14 bytes** per entity per tick (half-precision)
- **Zero per-frame cost** for interaction detection (event-driven via `OnControllerColliderHit`)
- **Periodic resync** every 5s corrects drift; skipped when idle
- **Server:** zero-copy forwarding, multi-room, ~400 lines of Rust

---

## License

MIT

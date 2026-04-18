# NetworkManager

The root of the networking system. One per scene. Handles the WebSocket connection, deterministic object registration, shared clock, local-player tracking, runtime spawn / despawn.

Drop a NetworkManager on any scene GameObject; set `Server URL` and `Room ID` in the inspector; everything else happens automatically.

## Inspector properties

### Connection

| Property | Default | Description |
|---|---|---|
| `serverUrl` | `ws://localhost:9090` | Relay endpoint. No trailing room — that's set below. |
| `roomId` | `""` → `default` | Clients in the same room share a physics world. Use per-dungeon / per-lobby rooms for isolation. |
| `reconnectDelay` | `3` s | How long to wait before retrying after a disconnect. |

### Sync rates

| Property | Default | Description |
|---|---|---|
| `sendRate` | `60` Hz | Held-object position send rate. Only fires for objects this client owns. |
| `playerPosSendRate` | `10` Hz | `MSG_PLAYER_POS` rate — drives server-side AOI filtering. |
| `positionThreshold` | `0.01` m | Minimum pose delta before a held-object update is included. |
| `rotationThreshold` | `1°` | Same for rotation. |

### Local player

| Property | Default | Description |
|---|---|---|
| `localPlayer` | *none* | Your local player transform. If empty, auto-detected from Somnium rig (`Original Player: ...`). |
| `autoAttachPushDetector` | `false` | Add `PlayerPushDetector` to the resolved local player. The server's Rapier capsule handles continuous push physics from normal movement; enable this only if you specifically want one-shot impulses on contact. |

### Runtime spawning

| Property | Default | Description |
|---|---|---|
| `networkedPrefabs` | `[]` | Prefabs eligible for runtime spawn via `CTRL_SPAWN`. Order = prefab index. **Must be the same on every client.** |

## Runtime API

### Singleton + state

```csharp
public static NetworkManager Instance { get; }    // null until Awake

public bool    IsConnected      { get; }
public bool    IsNetworkReady   { get; }          // welcome received, safe to send
public bool    IsPhysicsReady   { get; }          // server Rapier world is live
public ushort  ClientId         { get; }          // assigned by server (1..65535)
public Transform LocalPlayer    { get; }          // resolved local player, if any
```

### Shared clock

```csharp
public double  ServerTime           { get; }   // seconds, NTP-refined, shared across room
public ulong   SceneReadyServerMs   { get; }   // unix ms when physics went live
```

`ServerTime` converges within a few ms across all clients in the room. Use it for any deterministic timing you need to agree on (NPC wander, synced animations, countdowns).

`SceneReadyServerMs` is the shared "scene epoch" — broadcast in `CTRL_WELCOME` and `CTRL_PHYSICS_READY`. Scene-baked `NetworkObject`s derive their `SpawnServerMs` from it.

### Events

```csharp
public event Action                          OnNetworkReady;
public event Action                          OnDisconnected;
public event Action<NetworkObject, int>      OnObjectSpawned;   // obj, prefabIdx
public event Action<NetworkObject>           OnObjectDespawned;
```

### Methods

```csharp
// Override the local player transform. Called automatically by
// SomniumBridgeIntegration; call manually if you have a custom rig.
void SetLocalPlayer(Transform t);

// Ship raw bytes over the websocket. Low-level escape hatch — you
// almost never need this.
void SendBinaryPublic(byte[] data);
```

## Common patterns

### Minimum scene setup

```
Scene
├── "Networking"              (empty GameObject)
│   ├── NetworkManager        (serverUrl, roomId)
│   └── SomniumBridgeIntegration
├── "Floor"                   (static)
├── "Cube"
│   ├── Rigidbody
│   └── NetworkObject
└── "Player"                  (your rig)
```

That's a complete networked scene. Cube syncs automatically.

### Wait for the network, then do something

```csharp
public class SpawnOnReady : MonoBehaviour
{
    public GameObject prefab;

    void Start()
    {
        var net = NetworkManager.Instance;
        if (net == null) return;

        if (net.IsNetworkReady) OnReady();
        else                    net.OnNetworkReady += OnReady;
    }

    void OnReady()
    {
        Debug.Log($"I'm client {NetworkManager.Instance.ClientId}");
        // safe to send variables / events / etc.
    }
}
```

### Runtime-spawned prefabs

Runtime spawns require the prefab to be listed on every client's NetworkManager. Order defines the `prefabIdx` the server sends.

```csharp
// Any client can request a spawn. Server assigns the id and broadcasts.
void SpawnBarrel(Vector3 where)
{
    var net = NetworkManager.Instance;
    byte prefabIdx = 2;  // whichever slot "barrel" sits in networkedPrefabs

    // The lowest-level way: build a CTRL_SPAWN_REQUEST manually.
    // (A helper wrapper is on the roadmap — for now this is what it looks like.)
    // See the NetworkManager.Sender partial for the helper once it lands.
}

// React to spawns anywhere in your scene
void OnEnable()
{
    NetworkManager.Instance.OnObjectSpawned += HandleSpawned;
}
void OnDisable()
{
    if (NetworkManager.Instance != null)
        NetworkManager.Instance.OnObjectSpawned -= HandleSpawned;
}
void HandleSpawned(NetworkObject obj, int prefabIdx)
{
    if (prefabIdx == 2)
        obj.GetComponent<NetworkVariables>()?.Set("spawnedBy", "dungeon-event");
}
```

### Using the shared clock

```csharp
// Every client reads the same value at the same wall-clock instant (±few ms).
double nowSec = NetworkManager.Instance.ServerTime;

// Scene-relative time: how long since physics came up in this room?
ulong sceneMs = NetworkManager.Instance.SceneReadyServerMs;
if (sceneMs != 0)
{
    double sinceSceneReady = NetworkManager.Instance.ServerTime - sceneMs / 1000.0;
    // ...drive a deterministic clock hand, door schedule, boss phase timer, etc.
}
```

### Reconnect awareness

```csharp
void OnEnable()
{
    NetworkManager.Instance.OnDisconnected += () => UiBanner.Show("Reconnecting…");
    NetworkManager.Instance.OnNetworkReady += () => UiBanner.Hide();
}
```

On disconnect, in-flight claims are released automatically and the connect loop retries every `reconnectDelay` seconds. You just need to re-show UI if you care.

## Extending NetworkManager

`Awake`, `Start`, `OnDestroy`, and `Update` are all `protected virtual`. Subclass if you want a drop-in replacement with extra behaviour:

```csharp
public class MyNetworkManager : NetworkManager
{
    protected override void Start()
    {
        base.Start();
        SomniumBridge.PlayersContainer.LocalPlayerJoined += _ =>
            Debug.Log("Local player joined — Somnium bridge resolved");
    }
}
```

Since it's a `partial` class split across Receiver / Sender, your subclass stays in its own file and doesn't conflict with the partials.

## Don't

- **Don't** put two NetworkManagers in a scene. The singleton kicks the second one out but now nothing is set up right.
- **Don't** assume `Instance` is non-null in your own `Awake` — execution order isn't guaranteed. Either use the `OnNetworkReady` event or check `Instance != null`.
- **Don't** call `SendBinaryPublic` with hand-built messages unless you know the protocol spec. There are per-component senders for the common cases.
- **Don't** change `networkedPrefabs` order after building. Prefab indices are implicit — shuffling them breaks every client's view of existing spawns.

## Related

- [**NetworkObject**](./network-object) — what you attach to the things NetworkManager registers
- [**SomniumBridgeIntegration**](./) — auto-hooks the Somnium rig + account UUID into NetworkManager
- [**NetworkPersistence**](./network-persistence) — per-player save/load that piggybacks on NetworkManager's connection
- [Protocol overview](../protocol/overview) — what's actually going over the wire

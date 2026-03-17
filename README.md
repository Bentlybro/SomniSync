# Somnium Network - Custom Networking System

Fast, scalable networking for Somnium Space worlds. Breaks the 100 object limit.

## Quick Start

### Server

1. **Build and run:**
   ```bash
   cd server/SomniumNetwork.Server
   dotnet run
   ```
   
   Server starts on `ws://localhost:9090` and spawns 100 test entities.

### Unity Client

1. **Copy Unity scripts to your project:**
   - Copy `client/Unity/` folder to `Assets/Scripts/SomniumNetwork/`

2. **Create Network Manager:**
   - Create empty GameObject in scene
   - Add `NetworkManager` component
   - Set Server URL: `ws://localhost:9090/`
   - Assign Entity Prefab (see step 3)

3. **Create Entity Prefab:**
   - Create a Cube (or any GameObject)
   - Add `NetworkEntity` component
   - Add `NetworkTransform` component
   - Save as prefab
   - Assign to NetworkManager

4. **Run scene:**
   - Press Play
   - Should connect to server and spawn 100 cubes moving in circles

---

## Components

### NetworkManager
**Main network coordinator. Add to scene once.**

Fields:
- `Server URL` - WebSocket server address
- `Auto Connect` - Connect on Start()
- `Entity Prefab` - Prefab to spawn for networked entities

### NetworkEntity
**Marks GameObject as networked.**

Fields:
- `Entity ID` - Unique identifier (auto-generated)
- `Entity Type` - Type string (cube, npc, etc.)
- `Spawn On Start` - Register with NetworkManager on Start()
- `Destroy On Disconnect` - Cleanup when connection lost

### NetworkTransform
**Syncs position/rotation from server.**

Fields:
- `Sync Position` - Sync position updates
- `Sync Rotation` - Sync rotation updates
- `Sync Scale` - Sync scale updates
- `Interpolation Speed` - How fast to lerp (1-50)
- `Use Interpolation` - Smooth vs instant updates

---

## Architecture

```
[C# Server]
  ├─ Spawns entities
  ├─ Runs game logic (AI, physics, etc.)
  ├─ Sends updates via WebSocket
  └─ 20 tick/sec update rate
  
      ↓ WebSocket (JSON) ↓
      
[Unity Client]
  ├─ Receives spawn/update/despawn
  ├─ Creates/moves/destroys GameObjects
  └─ Smooth interpolation for movement
```

---

## Message Protocol

### Spawn Entity
```json
{
  "Type": "spawn",
  "EntityId": "entity_0",
  "EntityType": "cube",
  "Position": { "X": 0, "Y": 0, "Z": 0 },
  "Rotation": { "X": 0, "Y": 0, "Z": 0 }
}
```

### Update Entities
```json
{
  "Type": "update",
  "Updates": [
    {
      "Id": "entity_0",
      "Position": { "X": 1.5, "Y": 0, "Z": 0 },
      "Rotation": { "X": 0, "Y": 45, "Z": 0 }
    }
  ]
}
```

### Despawn Entity
```json
{
  "Type": "despawn",
  "EntityId": "entity_0"
}
```

---

## Customizing Server Logic

Edit `GameServer.cs` → `RunGameLoopAsync()`:

```csharp
// Example: Random movement instead of circles
foreach (var entity in _entities.Values)
{
    entity.Position.X += Random.Range(-1f, 1f) * deltaTime;
    entity.Position.Z += Random.Range(-1f, 1f) * deltaTime;
}
```

---

## Performance

- **Tested:** 100 entities @ 20 Hz (2000 updates/sec)
- **Server:** ~5% CPU on modest hardware
- **Client:** Smooth 60+ FPS in Unity
- **Latency:** 10-50ms (local network)

**No Photon limits. Scale as needed.**

---

## Roadmap

- [ ] NetworkAnimator (sync animator parameters)
- [ ] NetworkVariable<T> (sync custom data)
- [ ] Area of Interest (AOI) system
- [ ] Distance-based tick rates
- [ ] Binary protocol (reduce bandwidth)
- [ ] Server templates (dungeon, PvP, etc.)

---

## Requirements

- .NET 8.0 SDK (server)
- Unity 2021.3+ (client)
- Somnium Space SDK

---

## License

MIT

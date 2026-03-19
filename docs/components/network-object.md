# NetworkObject

Add to any GameObject you want synced across clients. Handles position/rotation sync, ownership, and interaction.

## Inspector Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `entityType` | string | `""` | Type identifier used for deterministic sorting (e.g. `"cube"`, `"ball"`, `"door"`) |
| `smoothing` | SmoothingMode | `Medium` | Interpolation smoothing for spectators |

### Smoothing Modes

| Mode | Duration | Use Case |
|------|----------|----------|
| `None` | 0ms | Snap to position (teleporting objects) |
| `Low` | 33ms | Fast-moving objects (projectiles) |
| `Medium` | 50ms | General purpose (default) |
| `High` | 100ms | Slow-moving objects (doors, platforms) |

## Key Properties (Code)

```csharp
ushort LocalId           // Deterministic ID (assigned by sorting)
bool IsClaimedLocally    // This client owns this object
bool IsClaimedByRemote   // Another client owns this object
ushort ClaimedByClient   // Client ID of current owner (0 = master authority)
```

## Public Methods

```csharp
// Manually claim/release (for custom interaction systems)
void Grab()      // Claim ownership, enable local physics
void Release()   // Release ownership, return to master authority

// Called by PlayerPushDetector (automatic)
void OnPlayerPush()   // Auto-claim on player collision
```

## How Interaction Works

1. Player walks into object → `PlayerPushDetector` calls `OnPlayerPush()`
2. Object is **claimed** — physics enabled locally, position updates sent
3. If this object hits another NetworkObject → **chain claim** (domino effect)
4. Once object velocity ≈ 0 → **auto-released** back to master authority
5. 0.5s cooldown prevents claim/release loops

## Deterministic Sorting

All clients sort NetworkObjects by:
1. `entityType` (alphabetical)
2. `gameObject.name` (alphabetical)
3. Position: x → y → z

This produces identical `LocalId` assignments on every client without any network handshake.

## Tips

- Set unique `entityType` values for different object categories
- Objects with the same type and name are sorted by position — make sure they're in the same positions on all clients
- Use `Grab()` / `Release()` for custom interactions (e.g. picking up items with a button press)

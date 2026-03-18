# Photon Fusion Compatibility Guide

## Goal
Build a networking system that **works exactly like Photon Fusion** from a world builder's perspective, but uses our custom WebSocket backend instead of Photon Cloud.

## Why Mirror Fusion?

1. **World builders already know it** — Somnium uses Fusion, so our API should match
2. **Proven patterns** — Fusion solved these problems already
3. **Drop-in replacement** — Minimal changes to switch from Fusion to our system

---

## Core Components (Matching Fusion's API)

### 1. NetworkObject
**Fusion equivalent:** `NetworkObject`

**Purpose:** Core network identity. Every networked GameObject needs this.

**Fields:**
- Object Type (for prefab spawning)
- State Authority (which client controls it)
- Spawn settings

**Usage:**
```csharp
// Add to any GameObject you want to network
gameObject.AddComponent<NetworkObject>();
```

---

### 2. NetworkTransform
**Fusion equivalent:** `NetworkTransform`

**Purpose:** Sync position/rotation for non-physics objects.

**Use for:**
- NPCs (server-controlled AI movement)
- Teleporting objects
- Anything without Rigidbody physics

**Usage:**
```csharp
// GameObject with NetworkObject
gameObject.AddComponent<NetworkTransform>();
```

---

### 3. NetworkRigidbody3D
**Fusion equivalent:** `NetworkRigidbody3D`

**Purpose:** Sync Unity Rigidbody physics across network.

**Use for:**
- Balls, throwable objects
- Physics props
- Any object with real Unity physics

**Settings (matching Fusion):**
- State Authority
- Sync Scale
- Sync Parent
- Interpolation Target (Transform vs Rigidbody)
- Use Precise Rotation
- Render Sleep Threshold

**Usage:**
```csharp
// GameObject with NetworkObject + Rigidbody
gameObject.AddComponent<NetworkRigidbody3D>();
```

---

### 4. NetworkAnimator (TODO)
**Fusion equivalent:** `NetworkAnimator`

**Purpose:** Sync animator parameters and states.

---

## State Authority System

**Concept from Fusion:** Who controls which object?

- **Server Authority (0):** Server controls, clients render
- **Client Authority (1+):** One client controls, server relays to others

**How it works:**
1. Ball spawns with State Authority = 0 (server controls)
2. Player grabs ball → `RequestStateAuthority()`
3. Server grants authority → that client's physics simulation is now authoritative
4. Other clients receive physics updates, interpolate smoothly
5. Player drops ball → authority returns to server (or next grabber)

**Benefits:**
- Prevents conflicts (two players grabbing same ball)
- Smooth physics (authority client simulates locally, no network lag)
- Server can override if needed

---

## Network Spawning

**Fusion pattern:**
1. World builder places prefabs in scene with NetworkObject
2. At runtime, NetworkObject registers with NetworkManager
3. Server/client spawns objects dynamically via `NetworkManager.Spawn(prefab)`

**Our implementation:**
- Scene objects: Spawn on Start (automatic registration)
- Runtime spawning: `NetworkManager.Spawn(prefabName, position, rotation)`

---

## Prefab Registry

**Fusion pattern:** Register prefabs in NetworkProjectConfig asset.

**Our implementation:**
```csharp
// NetworkManager Inspector
Entity Prefabs:
  - "zombie" → ZombiePrefab
  - "ball" → BallPrefab
  - "door" → DoorPrefab
```

When server spawns "zombie", clients look up ZombiePrefab and instantiate it.

---

## Network Properties (TODO)

**Fusion:** `[Networked] public int Health { get; set; }`

**Our equivalent:** `NetworkVariable<int>`

```csharp
public NetworkVariable<int> health = new NetworkVariable<int>(100);

// Subscribe to changes
health.OnValueChanged += (oldValue, newValue) => {
    UpdateHealthBar(newValue);
};
```

---

## RPCs (TODO)

**Fusion:** `[Rpc(RpcSources.All, RpcTargets.All)]`

**Our equivalent:**
```csharp
public void RPC_TakeDamage(int damage) {
    // Sent to server, server broadcasts to all clients
}
```

---

## What We're NOT Implementing (For Now)

- Lag compensation / rollback (Fusion's prediction system)
- Client-side prediction
- Network arrays/dictionaries
- Tick-based networking (we're using continuous updates)

**Why:** These are advanced features. Our MVP focuses on:
- Spawning networked objects
- Syncing transforms/rigidbodies
- State authority
- Basic RPCs

We can add prediction later if needed.

---

## Migration Path (Fusion → Our System)

**For world builders:**

1. Remove Photon Fusion packages
2. Import our package
3. Replace components:
   - `Fusion.NetworkObject` → `CustomNetworking.NetworkObject`
   - `Fusion.NetworkTransform` → `CustomNetworking.NetworkTransform`
   - `Fusion.NetworkRigidbody3D` → `CustomNetworking.NetworkRigidbody3D`
4. Update NetworkManager settings (server URL instead of Photon App ID)
5. Done!

**API should be identical enough that most scripts don't need changes.**

---

## Current Status

✅ NetworkObject (basic)  
✅ NetworkTransform (basic position/rotation sync)  
✅ NetworkRigidbody3D (physics sync skeleton)  
⚠️ State Authority (partially implemented)  
❌ NetworkAnimator  
❌ NetworkVariable<T>  
❌ RPCs  

**Next priorities:**
1. Finish state authority system
2. Proper rigidbody sync (velocity, angular velocity, sleeping)
3. NetworkAnimator
4. NetworkVariable<T>

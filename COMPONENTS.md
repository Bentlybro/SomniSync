# Component Guide

## Two Types of Networked Objects

### 1. Server-Authoritative (NPCs, Enemies)
**Server controls position, client just renders.**

**Components needed:**
- `NetworkEntity`
- `NetworkTransform`
- **NO Rigidbody** (or kinematic only)

**Use for:**
- NPCs with AI
- Enemies
- Server-controlled objects
- Anything where server decides movement

**Example:**
```
Zombie (Prefab)
├─ NetworkEntity
├─ NetworkTransform
└─ (NO Rigidbody)
```

Server runs AI, sends position updates. Client smoothly renders.

---

### 2. Client-Physics (Interactive Objects)
**Client simulates physics, syncs to server, server broadcasts to others.**

**Components needed:**
- `NetworkEntity`
- `NetworkRigidbody` (replaces NetworkTransform)
- `Rigidbody` (normal Unity component)
- `Collider`

**Use for:**
- Balls you can throw
- Physics props
- Grabbable/interactive objects
- Doors, levers, anything with client-side physics

**Example:**
```
Ball (Prefab)
├─ NetworkEntity
├─ NetworkRigidbody  ← handles physics sync
├─ Rigidbody         ← Unity physics
└─ SphereCollider
```

You grab it, throw it, physics runs locally. Position updates sent to server. Other clients see smooth interpolation.

---

## Component Details

### NetworkEntity
**Required on ALL networked objects.**
- Registers with NetworkManager
- Assigns unique ID
- Tells server when spawned/despawned

### NetworkTransform
**For server-controlled position (NPCs).**
- Receives position/rotation from server
- Smooth interpolation
- Client cannot modify

### NetworkRigidbody
**For client-controlled physics (interactive objects).**
- Has real Rigidbody physics
- Owner can modify (throw, push, grab)
- Sends updates to server
- Other clients interpolate
- Ownership system (only one client controls at a time)

---

## Ownership System (NetworkRigidbody)

**Problem:** Multiple players try to grab the same ball — who controls it?

**Solution:** Ownership
- First player to grab it becomes owner
- Owner's physics simulation is authoritative
- Other clients see interpolated version
- When owner releases, ownership can transfer

**In your scripts:**
```csharp
// When player grabs object
ball.GetComponent<NetworkRigidbody>().RequestOwnership();

// When granted (server callback)
// Now you can throw it, physics runs locally
```

---

## What to Use When

| Object Type | Components |
|-------------|------------|
| NPC/Enemy (server AI) | NetworkEntity + NetworkTransform |
| Ball/Throwable | NetworkEntity + NetworkRigidbody + Rigidbody |
| Door (physics hinge) | NetworkEntity + NetworkRigidbody + Rigidbody + HingeJoint |
| Pickup item (no physics) | NetworkEntity + NetworkTransform |
| Player avatar | Keep using Photon (unchanged) |

---

## Performance Tips

- **Update rate:** NetworkRigidbody sends at 10 Hz by default (adjustable)
- **Interpolation:** Smooth on other clients, feels good even at low update rates
- **Ownership:** Prevents conflicts, only one client simulates physics
- **Sleep:** Unity Rigidbody auto-sleeps when stationary (no updates sent)

---

## Next: NetworkAnimator

For syncing animations (walk, attack, etc.) — coming soon!

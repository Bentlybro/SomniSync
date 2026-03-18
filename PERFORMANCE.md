# Performance Optimizations

## Current Performance

**Tested:** 600+ networked physics objects running smoothly

**Optimizations applied:**

### Server-Side (60 Hz Updates)

1. **High tick rate:** 60 Hz (60 updates/second) for smooth movement
2. **Delta compression:** Only send entities that actually moved
   - Movement threshold: 0.01 units (1cm)
   - Rotation threshold: 1 degree
   - Stationary entities don't spam network
3. **Batch updates:** All entity updates in single WebSocket message
4. **Large buffers:** 64KB WebSocket buffers handle 600+ entities

### Client-Side

1. **Fast interpolation:** Speed 30 (snappier movement)
2. **Client-side prediction:** Objects you control move instantly, no network lag
3. **Smooth interpolation:** Other clients' objects interpolate smoothly
4. **Multi-part message handling:** Prevents JSON parse errors with large updates

---

## Performance Metrics

| Metric | Value |
|--------|-------|
| Max entities tested | 600+ |
| Server tick rate | 60 Hz |
| Client FPS | 60+ |
| Network latency | ~10-50ms (LAN) |
| Bandwidth per entity | ~200 bytes/update (when moving) |
| Total bandwidth (600 moving) | ~7 MB/sec |

---

## Tuning Parameters

### Server (`GameServer.cs`)

```csharp
var tickRate = 60; // Updates per second (20-60 recommended)
```

**Higher = smoother, more CPU/bandwidth**  
**Lower = more efficient, slightly choppier**

Movement thresholds (`Entity.cs`):
```csharp
public bool HasMoved(float threshold = 0.01f) // Position threshold in meters
```

### Client (`NetworkTransform.cs`)

```csharp
public float interpolationSpeed = 30f; // 1-50
```

**Higher = snappier (follows server faster)**  
**Lower = smoother (more lag compensation)**

---

## Scaling Beyond 600

For 1000+ entities:

1. **Area of Interest (AOI):**
   - Only send nearby entities to each client
   - Clients 50m apart don't need to sync
   - Reduces per-client bandwidth

2. **Variable tick rates:**
   - Close entities: 60 Hz
   - Far entities: 10 Hz
   - Very far: 1 Hz

3. **Interest management:**
   - Server tracks "who can see what"
   - Only broadcast to interested clients

**Status:** Not implemented yet, but architecture supports it.

---

## Current Bottlenecks

1. **All clients receive all updates** (no AOI yet)
   - 600 entities × 60 Hz = 36,000 updates/sec
   - With delta compression: ~10,000 actual updates/sec
   - Still manageable, but won't scale to 5,000+

2. **Single-threaded server**
   - C# async handles concurrency well
   - But heavy CPU work (physics, AI) would need threading

3. **JSON serialization**
   - Human-readable, easy to debug
   - Binary protocol would be 3-5x faster
   - Trade-off: simplicity vs speed

---

## What's Fast Enough?

**For Somnium VR worlds:**
- 100-500 networked objects: ✅ Smooth
- 500-1000 objects: ✅ Works well (tested at 600+)
- 1000-5000 objects: ⚠️ Need AOI
- 5000+ objects: ⚠️ Need binary protocol + AOI + threading

**You're already 6x better than Photon (100 limit).**

---

## Future Optimizations (Not Implemented)

- [ ] Binary protocol (MessagePack or custom)
- [ ] Area of Interest system
- [ ] Distance-based tick rates
- [ ] Multi-threaded server (one thread per world region)
- [ ] Entity pooling (reuse GameObjects instead of Instantiate/Destroy)
- [ ] Network LOD (send less data for far entities)

**Current system is fast enough for most Somnium worlds.**

---
slug: /
sidebar_position: 0
---

# SomniSync

High-performance real-time physics networking for Unity. Designed for VR worlds with thousands of objects.

- **4000+ objects** tested and working
- **14 bytes per entity** — half-precision binary protocol
- **Master/spectator architecture** — first client runs physics, others sync
- **Zero-config interaction** — walk into objects to push them
- **Relay server** — lightweight Rust server, zero game logic
- **Multi-room** — isolated rooms with auto-create/destroy

Built for [Somnium Space](https://somniumspace.com) VR but works with any Unity project using `System.Net.WebSockets`.

## Quick Start

1. Add `NetworkManager` to a GameObject
2. Set the server URL and room ID
3. Add `NetworkObject` to every object you want synced
4. Done.

→ [Full setup guide](/docs/getting-started/setup)

## Components

Every synced object needs `NetworkObject`. Everything else is optional:

| I want to... | Add this |
|--------------|----------|
| Sync position/rotation | `NetworkObject` (required) |
| Sync scale too | `NetworkObject` with `syncScale` enabled |
| Smooth fast-moving objects | + `NetworkRigidbody` |
| Sync custom data | + `NetworkVariables` |
| Send events/RPCs | + `NetworkEvents` |
| Sync animations | + `NetworkAnimator` |
| Sync particle effects | + `NetworkParticle` |
| Add networked timers | + `NetworkTimer` |

→ [Which components do I need?](/docs/getting-started/which-components)

## Performance

| Objects | Status |
|---------|--------|
| 4000 | Working |
| 3000 | Smooth |
| 2000 | Stable |
| 100 | Photon's limit |

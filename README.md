# SomniSync

High-performance real-time physics networking for Unity. Designed for VR worlds with thousands of objects. Relay-based architecture with binary protocol — tested with **4000+ objects** at 14 bytes per entity.

Built for [Somnium Space](https://somniumspace.com) VR but works with any Unity project using `System.Net.WebSockets`.

📖 **[Full Documentation](https://bentlybro.github.io/SomniSync/)**

## Quick Start

1. Add `NetworkManager` to a GameObject in your scene
2. Set the `Server URL` (e.g. `ws://localhost:9090`)
3. Set a `Room ID` (e.g. `my-world`)
4. Add `NetworkObject` to every object you want synced
5. Done.

That's it. Physics syncs automatically. Players can interact with objects by walking into them.

## Architecture

```
Master Client (first to join — runs physics for nearby objects)
    ↓ binary position updates (14 bytes/entity, half-precision)
Relay Server (Rust, zero-copy forwarding)
    ↓ broadcasts to all other clients
Spectator Clients (receive + apply positions, can interact)
    ↓ interaction updates for objects they grab/push
```

- **First client** = master (runs physics, sends position updates)
- **Everyone else** = spectators (receive updates, can interact with objects)
- **Master disconnects** → next client auto-promoted seamlessly
- **Server** = dumb relay. No game logic. Just pipes bytes.
- **Multi-room** — each room is isolated with its own master and objects. Rooms auto-create/destroy.
- **Area of Interest** — master only simulates objects near players. Objects far from all players are frozen.

## Features

- **10 networking components** — position sync, variables, RPCs, animations, velocity, particles, timers
- **Per-object interaction control** — pushable/grabbable flags per object
- **Scale sync** — optional toggle, adds 6 bytes per entity
- **Targeted RPCs** — `Send()` (all), `SendToAuthority()`, `SendToProxies()`
- **Connection events** — `OnNetworkReady`, `OnDisconnected`, `OnBecameMaster`, `OnBecameSpectator`
- **Client-side AOI** — configurable radius (default 50m), hysteresis to prevent flicker
- **Half-precision protocol** — 14 bytes per entity (position + rotation)
- **Deterministic IDs** — no GUID overhead, objects sorted identically on all clients

## Components

Every synced object needs **NetworkObject**. Everything else is optional.

| Component | What it does | Docs |
|-----------|-------------|------|
| **NetworkManager** | Connection, master/spectator logic, AOI, object registration | [→ Docs](docs/components/network-manager.md) |
| **NetworkObject** | Position/rotation/scale sync, ownership, interaction | [→ Docs](docs/components/network-object.md) |
| **NetworkVariables** | Sync custom data — int, float, bool, string | [→ Docs](docs/components/network-variables.md) |
| **NetworkEvents** | RPCs — broadcast, to authority, or to proxies | [→ Docs](docs/components/network-events.md) |
| **NetworkAnimator** | Sync Animator state, parameters, triggers | [→ Docs](docs/components/network-animator.md) |
| **NetworkRigidbody** | Sync velocity for smooth fast-moving objects | [→ Docs](docs/components/network-rigidbody.md) |
| **NetworkParticle** | Sync ParticleSystem play/stop and properties | [→ Docs](docs/components/network-particle.md) |
| **NetworkTimer** | Authority-driven networked countdowns | [→ Docs](docs/components/network-timer.md) |
| **NetworkStats** | F3 debug overlay — FPS, bandwidth, AOI status | [→ Docs](docs/components/network-stats.md) |
| **PlayerPushDetector** | Auto-attached collision detection (no setup needed) | [→ Docs](docs/components/player-push-detector.md) |

Not sure which to use? See [Which Components Do I Need?](docs/getting-started/which-components.md)

## Documentation

- [**Setup Guide**](docs/getting-started/setup.md) — Install, configure, connect
- [**Which Components?**](docs/getting-started/which-components.md) — Pick the right components for your objects
- [**Component Reference**](docs/README.md#components) — Detailed API for each component
- [**Protocol Spec**](docs/protocol/overview.md) — Binary format, message types, bandwidth
- [**Examples**](docs/README.md#examples) — Basic world, doors, physics playground, dungeon, vehicles, scoreboard

Or visit the full docs site: **[bentlybro.github.io/SomniSync](https://bentlybro.github.io/SomniSync/)**

## Server

Rust relay server — `tokio` + `tokio-tungstenite`, zero-copy forwarding, multi-room.

```bash
cd server
cargo build --release
./target/release/somnium-relay              # default: ws://0.0.0.0:9090
./target/release/somnium-relay 0.0.0.0:8080 # custom port
```

Minimal resource usage — handles hundreds of connections on a cheap VPS or home server.

## Performance

| Objects | Status |
|---------|--------|
| 4000 | Working (breakdown point) |
| 3000 | Smooth |
| 2000 | Stable |
| 100 | Photon's limit |

- **14 bytes** per entity per tick (half-precision floats)
- **Client-side AOI** reduces active objects to only what's near players
- **Zero per-frame cost** for interaction detection (event-driven)
- **Periodic resync** every 5s corrects drift

## License

MIT

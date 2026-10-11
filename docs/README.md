# SomniSync Documentation

Full documentation for SomniSync — high-performance real-time physics networking for Unity.

## Getting Started

New here? Start with these:

- [**Setup Guide**](getting-started/setup.md) — Install, configure, and connect
- [**Server Deployment**](getting-started/server-deployment.md) — Docker Compose, persistent data, TLS and operational limits
- [**Which Components Do I Need?**](getting-started/which-components.md) — Pick the right components for your objects

## Components

Every synced object needs `NetworkObject`. Everything else is optional.

### Core (required)

| Component | Description | Docs |
|-----------|-------------|------|
| **NetworkManager** | Connection, object registration, shared clock, AOI, local player tracking | [→ Docs](components/network-manager.md) |
| **NetworkObject** | Position/rotation sync, ownership, interaction (pushable/grabbable) | [→ Docs](components/network-object.md) |

### Optional

| Component | Description | Docs |
|-----------|-------------|------|
| **NetworkVariables** | Sync custom data — int, float, bool, string | [→ Docs](components/network-variables.md) |
| **NetworkEvents** | Fire-and-forget RPCs between clients | [→ Docs](components/network-events.md) |
| **NetworkAnimator** | Sync Animator state, parameters, and triggers | [→ Docs](components/network-animator.md) |
| **NetworkParticle** | Sync ParticleSystem play/stop and properties | [→ Docs](components/network-particle.md) |
| **NetworkTimer** | Authority-driven networked countdowns | [→ Docs](components/network-timer.md) |

### Utility

| Component | Description | Docs |
|-----------|-------------|------|
| **PlayerPushDetector** | Auto-attached collision detection (no setup needed) | [→ Docs](components/player-push-detector.md) |
| **NetworkStats** | F3 debug overlay — FPS, bandwidth, status | [→ Docs](components/network-stats.md) |

## Protocol

- [**Protocol Overview**](protocol/overview.md) — Binary format, message types, bandwidth

## Examples

| Example | What it shows | Docs |
|---------|--------------|------|
| **Basic World** | Minimal setup — physics objects + sync | [→ Docs](examples/basic-world.md) |
| **Interactive Door** | NetworkVariables + NetworkAnimator for persistent state | [→ Docs](examples/interactive-door.md) |
| **Physics Playground** | Throwables, destructible stacks, bowling with server-authoritative physics | [→ Docs](examples/physics-playground.md) |
| **Dungeon Room** | Full example — NPCs, traps, loot, timers, particles, combat | [→ Docs](examples/dungeon-room.md) |
| **Vehicle** | Driveable vehicle with enter/exit flow | [→ Docs](examples/vehicle.md) |
| **Scoreboard** | Non-physics game state sync with NetworkVariables | [→ Docs](examples/scoreboard.md) |

## Folder Structure

```
docs/
├── README.md                          ← you are here
├── getting-started/
│   ├── setup.md                       — installation + configuration
│   └── which-components.md            — component picker guide
├── components/
│   ├── network-manager.md             — connection + room management
│   ├── network-object.md              — position sync + ownership
│   ├── network-variables.md           — custom variable sync
│   ├── network-events.md              — RPCs / fire-and-forget events
│   ├── network-animator.md            — Unity Animator sync
│   ├── network-particle.md            — ParticleSystem sync
│   ├── network-timer.md               — networked countdowns
│   ├── network-stats.md               — debug overlay
│   └── player-push-detector.md        — auto collision detection
├── protocol/
│   └── overview.md                    — binary protocol specification
└── examples/
    ├── basic-world.md                 — simple synced scene
    ├── interactive-door.md            — door with networked state
    ├── physics-playground.md          — throwables, stacks, bowling
    ├── dungeon-room.md                — full dungeon with NPCs, traps, loot
    ├── vehicle.md                     — driveable vehicle
    └── scoreboard.md                  — game state sync
```

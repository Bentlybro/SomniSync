# SomniSync Documentation

Full documentation for SomniSync — high-performance real-time physics networking for Unity.

## Getting Started

New here? Start with these:

- [**Setup Guide**](getting-started/setup.md) — Install, configure, and connect
- [**Which Components Do I Need?**](getting-started/which-components.md) — Pick the right components for your objects

## Components

Every synced object needs `NetworkObject`. Everything else is optional.

### Core (required)

| Component | Description | Docs |
|-----------|-------------|------|
| **NetworkManager** | Connection, master/spectator logic, object registration | [→ Docs](components/network-manager.md) |
| **NetworkObject** | Position/rotation sync, ownership, interaction (pushable/grabbable) | [→ Docs](components/network-object.md) |

### Optional

| Component | Description | Docs |
|-----------|-------------|------|
| **NetworkVariables** | Sync custom data — int, float, bool, string | [→ Docs](components/network-variables.md) |
| **NetworkEvents** | Fire-and-forget RPCs between clients | [→ Docs](components/network-events.md) |
| **NetworkAnimator** | Sync Animator state, parameters, and triggers | [→ Docs](components/network-animator.md) |
| **NetworkRigidbody** | Sync velocity + angular velocity for smooth fast objects | [→ Docs](components/network-rigidbody.md) |
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

| Example | Description | Docs |
|---------|-------------|------|
| **Basic World** | Simple scene with synced physics objects | [→ Docs](examples/basic-world.md) |
| **Interactive Door** | Door with open/close state using NetworkVariables | [→ Docs](examples/interactive-door.md) |

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
│   ├── network-rigidbody.md           — velocity sync
│   ├── network-particle.md            — ParticleSystem sync
│   ├── network-timer.md               — networked countdowns
│   ├── network-stats.md               — debug overlay
│   └── player-push-detector.md        — auto collision detection
├── protocol/
│   └── overview.md                    — binary protocol specification
└── examples/
    ├── basic-world.md                 — simple synced scene
    └── interactive-door.md            — door with networked state
```

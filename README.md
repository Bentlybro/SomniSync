# SomniSync

SomniSync is a server-authoritative networking system for Unity and Somnium Space worlds. A Rust relay runs Rapier3D physics; Unity clients send player movement, interactions and game events, then receive compact object updates. Sleeping objects send no position updates. Distance filtering reduces traffic when the local player is known.

**Status:** tested as a working preview, with further real-world validation needed before a general release. The relay has no player authentication or private-room access control. [Deployment guidance](docs/getting-started/server-deployment.md) explains the current security boundaries.

## Quick start

### Relay

On a machine with Docker Compose:

```bash
git clone https://github.com/Bentlybro/SomniSync.git
cd SomniSync
docker compose up -d --build
docker compose logs -f relay
```

The game WebSocket is available on this host at `ws://127.0.0.1:9090`; the metrics dashboard is at `http://127.0.0.1:9091/`. Compose keeps SQLite data in a named volume. For public worlds, add a TLS reverse proxy and use its `wss://` address. The [deployment guide](docs/getting-started/server-deployment.md) includes a Caddy example, native Windows and Linux builds, and updates.

### Unity / Somnium world

Unity 6 (6000.0+) is required. In **Window → Package Manager → + → Add package from git URL**, use:

```text
https://github.com/Bentlybro/SomniSync.git?path=client/Unity
```

Open **Tools → SomniSync Package → Setup Wizard**. For a Somnium upload, first enter your creator ID and click **Prepare world scripts**. This creates `Assets/#User/<creator>.somnisync` with the correct `SomniumSpace.Worlds.<creator>.somnisync` namespace. Remove the generic Package Manager copy after the generated scripts compile, then reopen **Tools → SomniSync → Setup Wizard** from the world copy and add the manager and components. The [step-by-step guide](docs/getting-started/setup.md) covers migration if your scene already uses the generic package types.

Set a reachable relay base URL and room, add `NetworkObject` to supported colliders, run **Validate scene**, then test two clients with the same scene. Use `wss://` through a TLS reverse proxy for a public world. A `localhost` URL only works on your own PC.

## What is included

| Part | Purpose |
|---|---|
| `server/` | Rust WebSocket relay, Rapier physics, SQLite persistence store, local metrics dashboard |
| `client/Unity/` | Unity runtime components, setup wizard, scene validator, Somnium world-script exporter |
| `server/benchmarks/load_scene.py` | Thousands-of-objects WebSocket load test with measured per-client traffic |
| `benchmarks/bench.py`, `tools/bot_client.py` | Latency, connection and client-swarm tests |
| `docs/` | Setup, components, protocol, examples and operations |

Network updates use 22 bytes per compact position/rotation entity or 28 bytes when velocity is included, plus a seven-byte frame header. The relay broadcasts at up to 20 Hz, with near/mid/far area-of-interest tiers. Physics normally steps at 50 Hz and can reduce to 30 Hz under sustained load. `NetworkObject`, `NetworkVariables`, `NetworkEvents`, `NetworkAnimator`, `NetworkParticle`, `NetworkTimer`, `NetworkNPC`, runtime spawn/despawn, and optional player persistence are documented in the [component guide](docs/README.md).

## Measured performance

These are **specific test scenarios**, not guaranteed capacity. On an isolated two-vCPU VPS, reached through an SSH tunnel with roughly 47–62 ms median round-trip time depending on the run:

| Scene | Clients | Received per client | Relay physics step p95 | Drops |
|---|---:|---:|---:|---:|
| 4,000 moving spheres, AOI enabled | 4 | about 19.5 compact frames/s, 283 KB/s | 13.4 ms | 0 |
| 4,000 moving spheres, AOI enabled | 40 synthetic clients | 19.7 compact frames/s, 288 KB/s | 13.0 ms; broadcast p95 6.2 ms | 0 |
| 8,000 moving spheres, AOI enabled | 2 | about 20 compact frames/s, 480 KB/s | 28.7 ms at adaptive 30 Hz | 0 |

A generated Unity test scene connected through the VPS tunnel with 4,000 `NetworkObject` spheres and one static ground collider. Three additional bot clients joined the same room for 10 seconds, observed all 4,000 objects, and sent 489 held-object updates across six claim/release cycles. After the spheres settled, relay physics step p95 was 4.8 ms with zero dropped sends and about 48 MiB RSS. These tests used simple spheres; complex colliders, rendering, many rooms, longer sessions and real Somnium clients need separate validation. For a new server or world, run the [load benchmark](docs/ops/benchmarking.md) with your scene and player count before setting expectations.

## Operating boundaries

- The first client initializes a room's physics scene. Each Unity client supplies a scene compatibility fingerprint at join; the relay rejects a mismatched scene before registration. The fingerprint includes networked object transforms and supported static colliders. Increase the manager's **Scene version** when networking behavior or unsupported geometry changes. This fingerprint catches accidental mismatches; it is not player authentication.
- The relay limits itself to 512 concurrent connections, 128 rooms and 64 clients per room. A client that exceeds the inbound traffic budget is disconnected.
- Room names are public routing keys, not passwords. The relay does not authenticate players. Persistence is disabled by default because client-supplied account IDs are not proof of identity.
- The dashboard has no authentication and should stay on loopback or behind an authenticated proxy.
- The relay elects one writer for unclaimed `NetworkVariables`, `NetworkAnimator`, and `NetworkTimer` state. Persistent variable values replay to late joiners (up to 2 MiB per room), but the relay does not validate game rules such as health or score changes. Game-critical rules still need server-side validation before general release.
- The test scene in `SomniSyncTesting` is a development fixture; its `127.0.0.1` relay URL must be changed for uploaded worlds.

## Documentation

- [Setup](docs/getting-started/setup.md)
- [Server deployment](docs/getting-started/server-deployment.md)
- [Component reference](docs/README.md)
- [Protocol](docs/protocol/overview.md)
- [Benchmarking](docs/ops/benchmarking.md)

## License

SomniSync is licensed under the [Mozilla Public License 2.0](LICENSE). You may use it in your world; if you distribute modified SomniSync source files, those files remain under MPL 2.0. The license permits commercial redistribution.

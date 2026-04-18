# Getting Started

## Server

1. Install [Rust](https://rustup.rs/).
2. Build the relay:
   ```bash
   cd server
   cargo build --release
   ```
3. Run with two positional arguments — WebSocket bind + HTTP dashboard bind:
   ```bash
   # Windows
   .\target\release\somnium-relay.exe

   # Linux / macOS (production shape: game public, dashboard local-only)
   ./target/release/somnium-relay 0.0.0.0:9090 127.0.0.1:9091
   ```

Defaults: `ws://0.0.0.0:9090` for the game socket, `http://0.0.0.0:9091/` for the dashboard. If dashboard binding fails (port taken, etc.) the server logs a warning and keeps serving game traffic — the dashboard is strictly opt-in observability.

## Unity / Somnium client

1. Copy `client/Unity/` into your Somnium world scripts folder.
2. Add a **NetworkManager** component to a GameObject in the scene.
3. Set `Server URL` to your relay (e.g. `ws://localhost:9090`).
4. Set `Room ID` — clients in the same room share a physics world. Leave empty for `default`.
5. Add a **NetworkObject** component to every GameObject you want synced.
6. *(Optional)* Add **SomniumBridgeIntegration** — hooks the player's Somnium account UUID into persistence + AOI in one line.
7. *(Optional)* Add **NetworkStats** for an F3 debug overlay, or **NetworkDebug** for per-tick CSV dumps.

`PlayerPushDetector` attaches to the resolved local player automatically at runtime if `autoAttachPushDetector` is enabled — or just rely on the server's Rapier player capsule, which handles continuous push physics from your normal movement.

## What happens on connect

The server is always the physics authority. There's no "master" client to promote or fail over.

1. Client connects to `ws://server:9090/<room_id>`.
2. Server replies with `CTRL_WELCOME` carrying your client ID, a server-unix-ms timestamp, and the shared `scene_ready_ms` (0 if the room's physics hasn't been bootstrapped yet).
3. **First client in an empty room** also gets `needs_scene_init = 1` in the welcome. It replies with `MSG_SCENE_INIT` describing the scene's physics colliders, the server builds its Rapier world from that, and broadcasts `CTRL_PHYSICS_READY` to every current and future client.
4. Subsequent clients just start receiving `MSG_POSITION_VELOCITY` broadcasts 20 times a second.

Physics is simulated on the server at 50 Hz. Each client only receives updates for objects within its AOI radius (150 m by default, with closer objects broadcast more frequently).

## Namespace

All scripts use:

```csharp
namespace SomniumSpace.Worlds.bently.networking
```

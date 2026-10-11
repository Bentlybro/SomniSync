# Getting started

SomniSync has two parts: a Rust relay that runs outside the world, and Unity scripts that run in each player's world. Players in the same room must use the same scene, prefab list, and SomniSync version.

## 1. Start a relay

On a machine with Docker Compose, from the repository root:

```bash
docker compose up -d --build
docker compose logs -f relay
```

The game socket listens on the host's loopback at `ws://127.0.0.1:9090`. The dashboard is also available **only on the host's loopback** at `http://127.0.0.1:9091/`. The SQLite database stays in the `somnisync-data` Docker volume across container updates. Change the host bind address or ports with `SOMNISYNC_WS_HOST`, `SOMNISYNC_PORT`, and `SOMNISYNC_METRICS_PORT` environment variables before running Compose; see `.env.example`.

If you prefer a native binary, install Rust and run:

```bash
cd server
cargo build --locked --release
./target/release/somnium-relay 0.0.0.0:9090 127.0.0.1:9091
```

On Windows, use `target\release\somnium-relay.exe`. Run `somnium-relay --help` for bind and data-directory environment variables. Set `SOMNISYNC_DATA_DIR` to a persistent location for a service install. The relay creates `persistence.db` there.

For public players, point a DNS name at the server and use a TLS reverse proxy so the Unity URL is `wss://your-relay.example.com`. Keep the dashboard on loopback or behind authentication. See [server deployment](server-deployment.md) for a working Caddy example.

## 2. Install in Unity

Unity 6 (6000.0+) is required. In **Window → Package Manager → + → Add package from git URL**, enter:

```text
https://github.com/Bentlybro/SomniSync.git?path=client/Unity
```

Open **Tools → SomniSync Package → Setup Wizard** (it also opens once after import).

### Somnium world upload

Do this **before adding SomniSync components to a scene**:

1. Enter your Somnium creator ID in the wizard and click **Prepare world scripts**. The wizard copies the runtime and editor scripts to `Assets/#User/<creator>.somnisync` and changes their namespace to `SomniumSpace.Worlds.<creator>.somnisync`. Existing Unity `.meta` files are kept when updating.
2. Wait for Unity to compile. Remove the original SomniSync package in Package Manager, leaving the copy in `Assets/#User/`. This ensures the world uses the creator-specific scripts rather than the generic package assembly.
3. Reopen **Tools → SomniSync → Setup Wizard** from the copied editor scripts. Use its buttons to create the manager and add `NetworkObject` to selected objects.

If you already attached components from the generic package, replace those components with the copied world versions before removing the package; Unity cannot automatically change a component's script type across namespaces. Keep a scene backup while migrating an existing world.

For an ordinary Unity project that is not uploaded to Somnium, you can use the Package Manager components directly and skip the copy step.

## 3. Configure a scene

1. Set the relay base URL in the wizard, for example `ws://127.0.0.1:9090` for local testing or `wss://your-relay.example.com` for players. Do not put a room or query in the URL.
2. Choose a room containing at most 64 letters, numbers, `_`, `-`, or `.`. Every player in the same world must use the same room.
3. Click **Create or update Network Manager**. In a Somnium project with the `SOMNIUM_SDK` scripting define, the wizard also adds `SomniumBridgeIntegration` to resolve the local player for area-of-interest filtering.
4. Select each movable Box, Sphere, or Capsule Collider object and click **Add NetworkObject**. A Rigidbody enables server physics and grabbing. Static colliders without NetworkObject are sent to the server automatically. MeshColliders are not currently simulated by the relay.
5. Set **Scene version** to `1` for a new world. Increase it after changes to networking behavior or unsupported geometry that the fingerprint cannot detect. Click **Validate scene**, fix errors, save, and test with **Save scene and Play**. **Test relay connection** uses a disposable room to check the WebSocket endpoint.

The first client entering an empty room sends the collider description. The relay owns physics after that and broadcasts positions to later clients. A local player transform enables distance filtering; without one, each client may receive much more data. For the Somnium SDK integration, verify `SOMNIUM_SDK` is defined in the world assembly and the player rig is resolved at runtime.

## 4. Verify and operate

Run two real clients with the same scene, scene version and room. The relay rejects a different compatibility fingerprint before that client joins. Confirm they see the same moving objects, can claim and release them, and reconnect cleanly. Watch the dashboard for physics step time, outgoing bandwidth, and dropped messages. Use the [scene load benchmark](../ops/benchmarking.md) before claiming a player or object capacity for your hardware.

For local correctness checks, a relay on the same PC is fine. For latency and capacity results, place the relay and clients on separate machines and include real network delay. The current measured scenarios and limits are in the repository README.

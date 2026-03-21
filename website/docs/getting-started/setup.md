# Getting Started

## Setup

### Server

1. Install [Rust](https://rustup.rs/) if you don't have it
2. Build the relay server:
```bash
cd server
cargo build --release
```
3. Run:
```bash
# Windows
.\target\release\somnium-relay.exe

# Linux/macOS
./target/release/somnium-relay
```

Default: `ws://0.0.0.0:9090` — pass a custom address as the first argument.

### Unity/Somnium Client

1. Copy all files from `client/Unity/` into your Somnium world scripts folder
2. Add a **NetworkManager** component to any GameObject in your scene
3. Set the `serverUrl` to your relay server address (default: `ws://localhost:9090`)
4. Add a **NetworkObject** component to every object you want synced
5. Set `entityType` on each NetworkObject (e.g. `"cube"`, `"door"`, `"enemy"`)
6. *(Optional)* Add **NetworkStats** for an F3 debug overlay
7. *(Optional)* Add **NetworkVariables** to sync custom data (health, scores, door state, etc.)

That's it. `PlayerPushDetector` is attached to the local player automatically at runtime.

### How Roles Work

- **First client** to connect becomes the **master** (runs physics, authoritative)
- **All other clients** are **spectators** (receive state, can interact with objects)
- If master disconnects, the next client is **automatically promoted**

### Namespace

All scripts use:
```csharp
namespace SomniumSpace.Worlds.bently.networking
```

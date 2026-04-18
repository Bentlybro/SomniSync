# SomniSync — Quick Start Sample

A one-click-to-generate sample scene showing the minimum setup for a working SomniSync world. Use it as a reference or a starting point for your own scene.

## What this sample gives you

- A **NetworkManager** GameObject preconfigured for `ws://localhost:9090`
- A simple **player** (capsule + CharacterController + camera)
- A **ground plane** so physics objects have somewhere to land
- A **grabbable cube** (NetworkObject + BoxCollider + Rigidbody)
- A **wandering NPC** (NetworkObject + NetworkNPC)
- A **SpawnKeyTester** GameObject — press P at runtime to spawn from `networkedPrefabs`

## How to use it

1. Import the sample from the Package Manager window (`Window > Package Manager > SomniSync > Samples > Quick Start > Import`).
2. In the Unity menu bar: **Tools → SomniSync → Create Quick Start Scene**.
3. Unity will generate a new scene at `Assets/Samples/SomniSync/<version>/Quick Start/QuickStart.unity` and open it.
4. Start your SomniSync relay server:
   ```
   ./somnium-relay            # binds ws://0.0.0.0:9090 + http://127.0.0.1:9091
   ```
5. Press Play in Unity. Two copies of the scene running simultaneously (one in Play Mode, one in a built binary — or two Editor instances via ParrelSync) will show each other's players + cubes + NPCs.

## Where to go next

- Run **Tools → SomniSync → Validate Scene** against your own scene to catch common setup mistakes.
- Read the [full docs](https://bentlybro.github.io/SomniSync/) for everything the sample leaves out: persistence, NetworkVariables, NetworkEvents, custom NPCs, etc.

## Scope

This sample is intentionally minimal. It's not a template for a finished world — it's the shortest path from "package just imported" to "things are syncing." Once you've seen it work, delete it and build your actual scene.

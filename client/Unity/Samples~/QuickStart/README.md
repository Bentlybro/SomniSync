# SomniSync — Quick Start Sample

A one-click-to-generate sample scene showing the minimum setup for a working SomniSync world. Use it as a reference for an ordinary Unity project. For a Somnium upload, prepare your creator-specific scripts before adding components; this generated scene uses the generic package types.

## What this sample gives you

- A **NetworkManager** GameObject preconfigured for `ws://localhost:9090`
- A simple **player** (capsule + CharacterController + camera)
- A **ground cube with a BoxCollider** so server physics objects have somewhere to land
- A **grabbable cube** (NetworkObject + BoxCollider + Rigidbody)
- A **wandering NPC** (NetworkObject + NetworkNPC)
- A **SpawnKeyTester** GameObject — assign prefabs to `NetworkManager.networkedPrefabs`, then press P at runtime to test spawning

## How to use it

1. Import the sample from the Package Manager window (`Window > Package Manager > SomniSync > Samples > Quick Start > Import`).
2. In the Unity menu bar: **Tools → SomniSync → Create Quick Start Scene**.
3. Choose where to save the generated `QuickStart.unity` scene. Unity opens the new scene after saving it.
4. Start your SomniSync relay server:
   ```
   ./somnium-relay            # binds ws://0.0.0.0:9090 + http://127.0.0.1:9091
   ```
5. Press Play in Unity. Two copies of the same scene running simultaneously will see the same networked cube and NPC motion. The simple sample player rigs are local controls; avatar replication is outside this sample.

## Where to go next

- Run **Tools → SomniSync → Validate Scene** against your own scene to catch common setup mistakes.
- Read the [full docs](https://bentlybro.github.io/SomniSync/) for everything the sample leaves out: persistence, NetworkVariables, NetworkEvents, custom NPCs, etc.

## Scope

This sample is intentionally minimal. It's not a template for a finished world — it's the shortest path from "package just imported" to "things are syncing." Once you've seen it work, delete it and build your actual scene.

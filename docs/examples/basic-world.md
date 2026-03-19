# Example: Basic Physics World

The simplest setup — sync physics objects between players.

## Scene Setup

1. Create your Somnium world with objects (cubes, balls, etc.)
2. Add `Rigidbody` to every object you want physics on
3. Add `NetworkObject` to each one, set `entityType` (e.g. `"cube"`)
4. Add one `NetworkManager` to an empty GameObject
5. *(Optional)* Add `NetworkStats` for debug info

## That's It

- First player to join runs physics (master)
- Other players see everything synced in real-time
- Walk into objects to push them — works for all players
- If master leaves, next player takes over automatically

## Example Hierarchy

```
Scene
├── NetworkManager (NetworkManager component)
├── DebugUI (NetworkStats component) [optional]
├── Cube_001 (Rigidbody + NetworkObject [entityType: "cube"])
├── Cube_002 (Rigidbody + NetworkObject [entityType: "cube"])
├── Ball_001 (Rigidbody + NetworkObject [entityType: "ball"])
└── Ball_002 (Rigidbody + NetworkObject [entityType: "ball"])
```

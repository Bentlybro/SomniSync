# Example: Dungeon room

This is a component recipe for a shared room, not a drop-in combat system. SomniSync supplies physics, event relay, and component data transport; your world must define who is allowed to change health, loot, and encounter state.

## Scene setup

1. Create one `NetworkManager` with a room such as `dungeon-floor-1`. Give it a reachable relay URL and a local player transform so AOI filtering works.
2. Give every simulated skeleton or prop a Box, Sphere, or Capsule Collider, a `Rigidbody` when it should move under physics, and a `NetworkObject`. Use unique names and the same transforms on every client's scene.
3. Add `NetworkNPC` to skeletons that need deterministic idle wandering and chase behavior. Add an `Animator` and `NetworkAnimator` if animation parameters must be shared.
4. Add `NetworkEvents` for transient effects such as hit flashes, sounds, and explosion VFX. Register handlers on all clients, and make sure an event is sent only once per action.
5. Add `NetworkVariables` for live health or door state. The relay elects one writer for unclaimed objects and replays values marked persistent. Your world must still validate gameplay rules before the writer changes health or loot.
6. Use `NetworkParticle` for play/stop effects if needed. Keep physics props asleep when idle, and lower animation or particle sync rates where a change does not need frame-level updates.

## Encounter flow

- A player hits a skeleton: send one `NetworkEvents` damage request. The designated game-side writer validates it, updates health, and sends a hit effect. The current relay does not validate combat rules for you.
- A barrel hits a stack: Rapier handles the collision and broadcasts positions of the awake bodies. No chain claim or client master role exists.
- A boss countdown: `NetworkTimer` uses the elected writer and replays its latest variables. For a gameplay-critical encounter, validate rewards and completion on the server.
- A late joiner: scene physics, object positions, and persistent `NetworkVariables` values replay. Past `NetworkEvents` do not; encode lasting encounter state in variables.

Run [Validate Scene](../getting-started/setup.md) and test two real clients before expanding the room. See [which components to use](../getting-started/which-components.md) for the component list and [benchmarking](../ops/benchmarking.md) for load tests.

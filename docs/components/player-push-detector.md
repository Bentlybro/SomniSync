# PlayerPushDetector

**You don't need to add this manually.** NetworkManager attaches it automatically at runtime to the local player's body.

## How It Works

Somnium Space players use Unity's `CharacterController` for movement. CharacterController doesn't fire `OnCollisionEnter` on objects it hits — instead it fires `OnControllerColliderHit` on itself.

`PlayerPushDetector` is a small component added to the player's `NonXR.Body` (desktop) or `XR.Body` (VR) at runtime. It receives `OnControllerColliderHit` callbacks and notifies any `NetworkObject` that gets hit.

## Performance

Zero per-frame cost. `OnControllerColliderHit` is an event callback — it only fires when the player actually collides with something. No polling, no distance checks, no iteration over objects.

## Why Not OnCollisionEnter?

Unity's `CharacterController` doesn't participate in the normal physics collision system. It uses its own sweep-based movement. That's why we need `OnControllerColliderHit` specifically.

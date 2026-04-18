# NetworkStats

Optional debug overlay. Press **F3** to toggle.

## What It Shows

- Connection status (connected / disconnected)
- Physics-ready state (server Rapier world is live)
- Client ID
- Object count (registered NetworkObjects)
- Bytes sent / received per second
- Messages sent / received per second
- Current ServerTime offset + best RTT (clock-sync health)

## Setup

Add `NetworkStats` to any GameObject in your scene. That's it.

## Tips

- Useful during development and testing
- Remove or disable before publishing your world for production
- Stats update every second

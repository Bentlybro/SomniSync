# NetworkStats

Optional on-screen debug overlay. Press **F3** at runtime to toggle.

For production monitoring use the [server dashboard](../ops/dashboard) instead — NetworkStats is the in-game equivalent, useful during development.

## Setup

Drop the component on any GameObject. Done.

```
GameObject "Debug"
└── NetworkStats
```

## What it shows

| Row | Source |
|---|---|
| Connection status | `NetworkManager.IsConnected` |
| Physics ready | `NetworkManager.IsPhysicsReady` — the server Rapier world is live |
| Client ID | `NetworkManager.ClientId` |
| Objects | Count of registered `NetworkObject`s |
| Bytes sent / received per second | Rolling 1-second deltas of `NetworkManager.BytesSent` / `BytesReceived` |
| Messages sent / received per second | Same for `MessagesSent` / `MessagesReceived` |
| ServerTime offset | `NetworkManager.ServerTimeOffsetSeconds` — how far our local clock is from server-unix-time |
| Best RTT | Smallest round-trip seen since connect. A healthy WAN is 20-100 ms; persistently > 200 ms means laggy link |

## Reading the numbers

- **Bytes/sec spiking on the send side** while you're doing nothing → some local script is sending variables/events/animator state or held-object updates more often than expected.
- **Bytes/sec on receive scales with object count** — that's normal. AOI trims it, so moving far from busy areas should drop it.
- **Best RTT** is measured from `MSG_TIME_PING` / `CTRL_TIME_PONG` exchanges at 1 Hz. If it stays at `-1` / `∞`, the time-sync handshake isn't completing — usually means the server is down or unreachable.

## Custom bindings

The F3 toggle is hard-coded. To bind to a different key, subclass and override:

```csharp
public class MyStatsToggle : NetworkStats
{
    protected override void Update()
    {
        if (Input.GetKeyDown(KeyCode.F10)) /* toggle overlay */ ;
        base.Update();
    }
}
```

## Tips

- Remove or disable in production — the overlay takes a small IMGUI cost per frame.
- For long-running diagnostic data (histograms of tick time, bytes-over-time), use the server dashboard. NetworkStats is instantaneous; the dashboard is historical.

## Related

- [Server dashboard](../ops/dashboard) — the same kind of telemetry but richer, collected server-side
- `NetworkDebug` — structured per-tick Unity-console diagnostics; use `tools/bot_client.py` for CSV capture
- [Protocol overview](../protocol/overview) — explains what the bytes / messages actually are

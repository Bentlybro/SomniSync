# NetworkPersistence

Server-backed per-player key/value storage. SQLite on the server, typed API on the client, keyed by the player's stable identity.

Uses the same WebSocket the rest of the networking runs on — no separate HTTP call, no new server dependency. When explicitly enabled on a trusted relay, values survive reconnects and server restarts. Public relays do not enable this by default because the current client-supplied player ID is not authenticated. The relay advertises this capability in its welcome message. When it is disabled, Unity sends no player identity or persistence requests, and `Load` immediately returns its supplied default.

## Usage

```csharp
using SomniumSpace.Worlds.bently.networking;

// ───────── Save ─────────
// Typed overloads exist for float, int, bool, and string.
NetworkPersistence.Save("health", 87.5f);
NetworkPersistence.Save("coins",  1200);
NetworkPersistence.Save("has_key", true);
NetworkPersistence.Save("last_zone", "dungeon_03");

// ───────── Load ─────────
// Callback fires when the server responds. A default is always supplied
// so your code has a value even before the round-trip completes.
NetworkPersistence.Load("health", 100f, v => health = v);
NetworkPersistence.Load("coins",   0,   v => coins  = v);
NetworkPersistence.Load("last_zone", "", v => zone = v);

// ───────── Namespaces ─────────
// Optional ns: parameter keeps your data separate from other worlds'
// on the same relay server. Defaults to "global".
NetworkPersistence.Save("score", 1000, ns: "my_rpg");
NetworkPersistence.Load("score", 0, v => score = v, ns: "my_rpg");
```

`Save` is fire-and-forget. `Load` is asynchronous and takes a callback — the server replies with `CTRL_PERSIST_DATA` and the callback fires on the Unity main thread.

## Identity

Persistence is keyed by a stable per-player ID. The current relay does **not** verify that ID. Do not rely on it for private or valuable player data. On trusted local/dev relays only, set `SOMNISYNC_ALLOW_UNAUTH_PERSISTENCE=1`. Public deployment needs server-verifiable platform identity before enabling persistence. Client ID priority:

1. `NetworkPersistence.PlayerIdProvider` — a `Func<string>` you can set manually to whatever you like.
2. `PlayerPrefs` fallback — a device-local UUID generated on first use.

For Somnium, add **SomniumBridgeIntegration** to your scene and identity is wired up automatically:

```csharp
NetworkPersistence.PlayerIdProvider =
    () => SomniumBridge.PlayersContainer?.LocalPlayer?.Properties?.Id;
```

The bridge supplies the Somnium ID to the client, but this is not proof of account ownership to the relay. Keep persistence disabled on a public relay until token verification exists.

## Limits

- Player identity: ≤ 64 bytes UTF-8
- Namespace: ≤ 64 bytes UTF-8
- Key: ≤ 128 bytes UTF-8
- Value: ≤ 4096 bytes UTF-8 (server rejects larger writes with `CTRL_ERROR`)

Designed for quick typed values, not blobs. If you need to store a chunk of JSON, base64 an image, or similar, serialise to a string and save that — but a proper blob store is out of scope for now.

## On the server

`server/data/persistence.db` — SQLite in WAL mode:

```
player_data(player_id, namespace, key, value, updated_at)
PRIMARY KEY (player_id, namespace, key)
```

The `updated_at` column is populated server-side via `unixepoch()` on every `INSERT OR REPLACE` — useful for "who last changed this" queries if you ever care.

## Error semantics

- `Load` with a key that doesn't exist → callback fires with the supplied `defaultValue`.
- `Save` before the connection is ready → silent no-op (logged at warn level).
- Server write failure → logged server-side; client sees no error (Save is fire-and-forget).
- Server value too large → server sends `CTRL_ERROR` with the text `persist_value_too_large`.
- Network disconnect while a `Load` is pending → callback fires with `found = false` / default.

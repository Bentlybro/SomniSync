# Relay deployment

## Docker Compose

From a checkout of this repository:

```bash
docker compose up -d --build
docker compose ps
docker compose logs -f relay
```

The Compose file builds the Rust relay with the checked-in lockfile, runs it as an unprivileged user, and maps both the game socket and dashboard only to host loopback (`127.0.0.1:9090` and `127.0.0.1:9091`). Data lives in the named `somnisync-data` volume. To update, pull the desired revision and run `docker compose up -d --build` again. To stop, use `docker compose down`; the data volume remains. `docker compose down --volumes` removes the database too, so back it up first if you use persistence.

For another host port, set `SOMNISYNC_PORT` before starting Compose. To intentionally publish plain WebSocket directly, set `SOMNISYNC_WS_HOST=0.0.0.0`. The Unity URL must match the public host/port or your TLS proxy address. Use one relay per host port; do not bind over an existing service. `.env.example` lists these settings.

## Public `wss://` endpoint with Caddy

Give the host a DNS name such as `relay.example.com`, install Caddy on that host, and allow inbound ports 80 and 443. Keep the Compose relay on `127.0.0.1:9090`. Add this site block to your Caddyfile (merge it with your existing Caddyfile if you already serve other sites):

```caddyfile
relay.example.com {
    reverse_proxy 127.0.0.1:9090 {
        stream_close_delay 5m
    }
}
```

Replace the domain with your own, then validate and load the Caddyfile using your Caddy installation's normal service workflow. Set the Unity manager URL to `wss://relay.example.com` (no room path). Caddy obtains HTTPS certificates automatically for a reachable domain and handles WebSocket upgrades; `stream_close_delay` gives existing sessions time to leave when Caddy reloads. See the [Caddy HTTPS guide](https://caddyserver.com/docs/quick-starts/https) and [reverse proxy reference](https://caddyserver.com/docs/caddyfile/directives/reverse_proxy).

## Native binary

```bash
cd server
cargo build --locked --release
SOMNISYNC_DATA_DIR=/var/lib/somnisync ./target/release/somnium-relay 0.0.0.0:9090 127.0.0.1:9091
```

Create a dedicated service account and writable data directory. Keep the process running under your service manager. The two optional positional arguments override `SOMNISYNC_WS_BIND` and `SOMNISYNC_METRICS_BIND`. The defaults are `0.0.0.0:9090` and `127.0.0.1:9091`.

## Network and security

- Put a TLS reverse proxy in front of the public WebSocket socket and use `wss://` in uploaded worlds. The relay itself speaks plain WebSocket. Compose binds the relay port to loopback by default; keep that setting when the proxy runs on the same host.
- The dashboard and `/export` endpoint have no authentication. Keep port `9091` on loopback or add authentication at the proxy. For a remote dashboard, use an SSH tunnel such as `ssh -L 9091:127.0.0.1:9091 your-server`.
- Room names are public, not passwords. Anyone who knows the URL can join. The relay has no player authentication or room authorization. Do not use it for private rooms or trusted game actions until you add verifiable identity and access control.
- The relay accepts at most 512 simultaneous connections, 128 rooms, and 64 clients in one room. It disconnects a client that sends over 200 messages/s or 2 MiB/s for a sustained period (brief bursts are allowed). A room's scene fingerprint prevents accidental version mixing but does not authenticate players.
- Events have a separate 30 events/s and 64 KiB/s sustained sender budget, a 4 KiB frame cap, and strict parameter validation. These limits protect relay capacity; they do not decide whether an event is allowed by your game's rules.
- Player persistence is disabled by default because a client-supplied player ID cannot prove account ownership. `SOMNISYNC_ALLOW_UNAUTH_PERSISTENCE=1` is for trusted local testing only.
- Back up `persistence.db` (and its SQLite WAL files, if present) using a SQLite-aware backup or a stopped relay. Keep client and relay versions aligned when updating a world.

## Capacity checks

Start with a real Unity client to initialize the scene. Use the [load benchmark](../ops/benchmarking.md) and the dashboard to measure physics step time, network throughput, reconnects, and dropped sends on your own hardware. Thousands of sleeping bodies cost much less than thousands of moving bodies; object count alone is not a performance guarantee.

# Benchmarking

Use these tools against a disposable room on your own relay. Install Python 3.10+ and `websockets` (`python -m pip install websockets`) first.

- **`benchmarks/bench.py`** — ping, event relay latency, connection storm. Measures the relay fundamentals.
- **`tools/bot_client.py`** — headless bot swarm. Speaks the full binary protocol — the server genuinely can't tell these from Unity clients. Each bot writes a CSV of observed state (outside the Unity sandbox, so no file I/O inside the game).
- **`tools/netdbg_diff.py`** — offline CSV diff. Loads any two bot-client CSVs and reports inter-client position disagreement per-object.
- **`server/benchmarks/load_scene.py`** — initializes up to 16,384 generated physics bodies and records actual frames, bytes, and ping RTT per client. It can query relay metrics after the run.
- **`server/benchmarks/check_state_replay.py`** — checks writer election, rejection of unauthorized variable writes, late-join replay, and writer handoff in a disposable room.

## Scene load benchmark

In Unity, **Tools → SomniSync → Create 4,000 Object Test Scene** creates a separate scene with 4,000 networked sphere bodies, a static floor, a manager, and a local player position for AOI. It renders only one marker per 64 bodies so the test focuses on network and physics work. Change the manager URL from its local default to your relay or SSH tunnel, save the scene, and press Play. The scene starts with moving bodies that later settle; use the Python benchmark below for sustained moving-body traffic.

From the repository root, with a relay running on the default ports:

```bash
python server/benchmarks/load_scene.py --objects 4000 --clients 4 --aoi --seconds 10
```

For a remote relay, pass `--url ws://host:9090` (or `wss://...`) and `--metrics http://127.0.0.1:9091/metrics` through a private dashboard tunnel. The script creates a unique room and does not use your live world. `--aoi` reports a player position; omit it to measure the worst-case unfiltered downlink. This generates simple moving spheres, so test actual Unity content separately. The metrics endpoint is private by default; omit `--metrics` if it is not available.

A 40-client run with 4,000 generated moving spheres on an isolated two-vCPU VPS completed with 19.7 compact frames/s and 288 KB/s per synthetic client, 13.0 ms physics step p95, 6.2 ms broadcast p95, and zero dropped sends. Those clients shared one AOI position; this measures one load pattern rather than a capacity guarantee for arbitrary worlds.

## State replay check

Check component-state replay after changing either side of the wire protocol:

```bash
python server/benchmarks/check_state_replay.py --url ws://127.0.0.1:9090
```

## `benchmarks/bench.py`

```bash
# All tests against the default server
python benchmarks/bench.py --server ws://host:9090

# Just pings / just relay latency / just storm
python benchmarks/bench.py --server ws://host:9090 --test ping
python benchmarks/bench.py --server ws://host:9090 --test relay
python benchmarks/bench.py --server ws://host:9090 --test storm --storm 50
python benchmarks/bench.py --server ws://host:9090 --test scale --scale-max 50
```

What each test exercises:

- **Ping RTT** — pure WebSocket round-trip, no server logic. Lower bound on latency.
- **Relay latency** — one client sends MSG_EVENT (0x06), another receives. Tests the pure-relay path.
- **Scale** — N spectators receiving event broadcasts at 20 Hz. Measures latency vs client count.
- **Storm** — connect N clients simultaneously. Measures handshake under contention.

The obsolete client-authored position throughput test has been removed because the relay correctly ignores those packets.

## `tools/bot_client.py`

Swarm of headless clients that speak the full protocol: welcome, time-sync pings, player position at 10 Hz, receive position broadcasts, write a CSV per bot in the same schema as `NetworkDebug`.

```bash
# Start a real Unity/Somnium client first (so the scene is bootstrapped).
# Then launch a 40-bot swarm against the same room:
python tools/bot_client.py \
    --server ws://host:9090 --room my_world \
    --scene-fingerprint 0123456789abcdef \
    --count 40 --duration 180 --out-dir ./botlogs
```

With `--interact` added, each bot also cycles through claim → drive → release on random visible objects, stressing the server's Rapier write path and held-object relay:

```bash
python tools/bot_client.py ... --interact \
    --claim-cooldown 3 --hold-duration 2 --interaction-hz 60
```

Copy the 16-character fingerprint from the Unity console's `[Net] Scene ready` line; the value above is an example. Bots refuse to be first-in-room because they can't describe a SCENE_INIT. Always launch at least one real client first.

Each bot runs its own NTP-style clock sync with the same EMA alpha as the Unity client, so per-bot ServerTime estimates disagree independently — inter-client clock skew surfaces in the CSV exactly like it would between two real clients.

## `tools/netdbg_diff.py`

Given two CSVs (bot-vs-bot, bot-vs-real-client, or real-client-vs-real-client), joins rows per `npc_id` by nearest server-time and reports:

- Per-NPC euclidean position delta (n, mean, median, p95, max).
- Clock-skew stats between the two clients.

```bash
python tools/netdbg_diff.py A.csv B.csv --out diff.csv
```

Useful for validating that NPCs stay in sync across the swarm. A healthy result is mean < 0.5 m per NPC with clock skew tightly clustered. Widening range = EMA drift; constant bias mean = asymmetric network-path bias.

## Representative scenario

A realistic 40-user test needs:

1. 2-3 real Unity/Somnium clients so Unity-side bugs surface (GC pauses, render pressure, Somnium quirks).
2. 37-38 bots across 2-3 geographic regions so RTT distribution is realistic (bots on localhost report 1 ms RTTs that nothing in production will ever see).
3. Server's `/export` JSON captured at the peak.

Bots alone will faithfully stress the server side (CPU, broadcast bandwidth, Rapier tick budget) but won't reproduce client-side or network-path issues.

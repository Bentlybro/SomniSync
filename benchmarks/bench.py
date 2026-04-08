#!/usr/bin/env python3
"""
Somnium Network - Relay Benchmark Suite
========================================
Tests:
  1. WS Ping RTT          - raw network round-trip (no relay logic)
  2. Relay Latency        - master sends event -> spectator receives (p50/p95/p99)
  3. Relay Throughput     - messages/sec delivered at sustained send rate
  4. Scale Test           - latency & throughput vs. spectator count (1->N)
  5. Connection Storm     - time to connect N clients concurrently

Usage:
  python bench.py                        # run all tests, default server
  python bench.py --server ws://host:port
  python bench.py --test ping            # single test: ping|relay|throughput|scale|storm
  python bench.py --scale-max 100        # max spectators for scale test (default 50)
  python bench.py --duration 5           # seconds per throughput/relay test (default 5)
"""

import asyncio
import socket
import struct
import time
import statistics
import argparse
import uuid
from typing import List, Optional, Tuple
import websockets
from websockets.exceptions import ConnectionClosed

# ---------------------------------------------------------------------------
# Defaults
# ---------------------------------------------------------------------------
DEFAULT_SERVER   = "ws://159.65.56.232:9090"
PING_COUNT       = 200
RELAY_COUNT      = 500
THROUGHPUT_SECS  = 5
SCALE_STEPS      = [1, 5, 10, 25, 50]
STORM_CLIENTS    = 100

# ---------------------------------------------------------------------------
# Protocol helpers
# ---------------------------------------------------------------------------
MSG_EVENT            = 0x06
MSG_POSITION_VELOCITY = 0x0B
CTRL_PREFIX          = 0xFF
BENCH_EVENT_NAME     = b"__b"   # short name = less overhead


def make_event(obj_id: int, ts_ns: int) -> bytes:
    """
    NetworkEvent carrying a u64 nanosecond timestamp as its payload.
    [0x06][u16 obj_id][u8 target=All][u8 name_len][name][u8 param_count=1]
    [u8 type=0][u16 val_len=8][u64 ts_ns]
    """
    buf = bytearray()
    buf.append(MSG_EVENT)
    buf += struct.pack('<H', obj_id & 0xFFFF)
    buf.append(0)                          # target = All
    buf.append(len(BENCH_EVENT_NAME))
    buf += BENCH_EVENT_NAME
    buf.append(1)                          # param_count
    buf.append(0)                          # type = int (using as raw 8-byte carrier)
    buf += struct.pack('<H', 8)            # val_len
    buf += struct.pack('<Q', ts_ns)        # timestamp
    return bytes(buf)


def make_pos_velocity(client_id: int, seq: int, entity_count: int = 20) -> bytes:
    """
    MSG_POSITION_VELOCITY: simulates a master sending a batch of entity positions.
    [0x0B][u16 sender][u16 seq][u16 count][N x 20 bytes]
    Each entity: [u16 local_id][half×3 pos][u16×3 rot][half×3 vel]
    """
    buf = bytearray()
    buf.append(MSG_POSITION_VELOCITY)
    buf += struct.pack('<H', client_id & 0xFFFF)
    buf += struct.pack('<H', seq & 0xFFFF)
    buf += struct.pack('<H', entity_count)
    for i in range(entity_count):
        buf += struct.pack('<H', i)        # local_id
        buf += b'\x00\x00' * 3            # x,y,z as half=0.0
        buf += b'\x00\x00' * 3            # rx,ry,rz compressed
        buf += b'\x00\x00' * 3            # vx,vy,vz as half=0.0
    return bytes(buf)


def extract_event_ts(data: bytes) -> Optional[int]:
    """Extract the u64 timestamp from a bench event message, or None."""
    try:
        if data[0] != MSG_EVENT:
            return None
        offset = 4                         # type + obj_id(2) + target
        name_len = data[offset]
        offset += 1 + name_len            # skip name
        if data[offset] < 1:
            return None
        offset += 1                        # param_count
        # type byte + u16 len + 8 bytes
        offset += 1 + 2
        return struct.unpack_from('<Q', data, offset)[0]
    except Exception:
        return None


# ---------------------------------------------------------------------------
# Formatting
# ---------------------------------------------------------------------------
def fmt_ms(ns: float) -> str:
    return f"{ns / 1e6:.2f} ms"

def fmt_us(ns: float) -> str:
    return f"{ns / 1e3:.1f} µs"

def percentiles(samples: List[float]) -> Tuple[float, float, float, float]:
    s = sorted(samples)
    n = len(s)
    def p(pct): return s[int(pct / 100 * n)]
    return p(50), p(90), p(95), p(99)

def print_latency_table(label: str, samples: List[float], unit="ms"):
    fmt = fmt_ms if unit == "ms" else fmt_us
    p50, p90, p95, p99 = percentiles(samples)
    mean = statistics.mean(samples)
    print(f"  {label}")
    print(f"    mean={fmt(mean)}  p50={fmt(p50)}  p90={fmt(p90)}  p95={fmt(p95)}  p99={fmt(p99)}  n={len(samples)}")

def separator(char="-", width=60):
    print(char * width)


# ---------------------------------------------------------------------------
# Connection helper
# ---------------------------------------------------------------------------
async def connect(server: str, room: str = "bench"):
    uri = f"{server}/{room}"
    ws = await websockets.connect(uri, max_size=2**20, close_timeout=5)
    # Disable Nagle's algorithm on the client socket — same as the server-side fix.
    # Without this, the kernel may buffer small game packets for up to 40ms.
    try:
        sock = ws.transport.get_extra_info("socket")
        if sock:
            sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    except Exception:
        pass
    return ws


# ---------------------------------------------------------------------------
# TEST 1 - WS Ping RTT
# ---------------------------------------------------------------------------
async def test_ping(server: str, count: int = PING_COUNT):
    separator()
    print(f"TEST 1 - WebSocket Ping RTT  ({count} pings)")
    separator()

    ws = await connect(server, f"bench-ping-{uuid.uuid4().hex[:6]}")
    samples = []
    try:
        # Drain role assignment message
        await asyncio.wait_for(ws.recv(), timeout=2)

        for _ in range(count):
            t0 = time.perf_counter_ns()
            pong = await ws.ping()
            await pong
            samples.append(time.perf_counter_ns() - t0)
    finally:
        await ws.close()

    print_latency_table("Ping RTT", samples)
    print()
    return samples


# ---------------------------------------------------------------------------
# TEST 2 - Relay Latency
# ---------------------------------------------------------------------------
async def test_relay_latency(server: str, count: int = RELAY_COUNT):
    separator()
    print(f"TEST 2 - Relay Latency  (1 master + 1 spectator, {count} events)")
    separator()

    room = f"bench-relay-{uuid.uuid4().hex[:6]}"
    master = await connect(server, room)
    spec   = await connect(server, room)

    # Drain initial role messages
    await asyncio.wait_for(master.recv(), timeout=2)
    await asyncio.wait_for(spec.recv(), timeout=2)
    # Spectator may receive a snapshot request - drain up to 2 more with a short timeout
    for _ in range(2):
        try:
            await asyncio.wait_for(spec.recv(), timeout=0.2)
        except asyncio.TimeoutError:
            break

    samples = []

    async def receive_loop(target_count):
        received = 0
        while received < target_count:
            try:
                raw = await asyncio.wait_for(spec.recv(), timeout=3.0)
                if isinstance(raw, bytes):
                    ts_sent = extract_event_ts(raw)
                    if ts_sent is not None:
                        latency = time.perf_counter_ns() - ts_sent
                        samples.append(latency)
                        received += 1
            except asyncio.TimeoutError:
                print(f"  WARNING: timed out waiting for message {received+1}/{target_count}")
                break

    recv_task = asyncio.create_task(receive_loop(count))

    # Send from master
    seq = 0
    for _ in range(count):
        msg = make_event(0, time.perf_counter_ns())
        await master.send(msg)
        seq += 1
        await asyncio.sleep(0)  # yield

    await recv_task
    await master.close()
    await spec.close()

    if samples:
        print_latency_table("Relay Latency", samples)
    else:
        print("  No samples collected - server may not be relaying MSG_EVENT from master")
    print()
    return samples


# ---------------------------------------------------------------------------
# TEST 3 - Relay Throughput
# ---------------------------------------------------------------------------
async def test_throughput(server: str, duration_secs: float = THROUGHPUT_SECS, entity_count: int = 20):
    separator()
    print(f"TEST 3 - Relay Throughput  (flood for {duration_secs}s, {entity_count} entities/msg)")
    separator()

    room   = f"bench-tput-{uuid.uuid4().hex[:6]}"
    master = await connect(server, room)
    spec   = await connect(server, room)

    await asyncio.wait_for(master.recv(), timeout=2)
    await asyncio.wait_for(spec.recv(), timeout=2)
    for _ in range(2):
        try:
            await asyncio.wait_for(spec.recv(), timeout=0.2)
        except asyncio.TimeoutError:
            break

    sent_count    = 0
    recv_count    = 0
    recv_bytes    = 0
    sent_bytes    = 0
    seq           = 0
    deadline      = time.perf_counter() + duration_secs

    async def sender():
        nonlocal sent_count, sent_bytes, seq
        while time.perf_counter() < deadline:
            msg = make_pos_velocity(1, seq, entity_count)
            await master.send(msg)
            sent_count += 1
            sent_bytes += len(msg)
            seq += 1

    async def receiver():
        nonlocal recv_count, recv_bytes
        while time.perf_counter() < deadline + 0.5:
            try:
                raw = await asyncio.wait_for(spec.recv(), timeout=0.5)
                if isinstance(raw, bytes):
                    recv_count += 1
                    recv_bytes += len(raw)
            except asyncio.TimeoutError:
                if time.perf_counter() > deadline + 0.3:
                    break

    t0 = time.perf_counter()
    await asyncio.gather(sender(), receiver())
    elapsed = time.perf_counter() - t0

    await master.close()
    await spec.close()

    send_rate = sent_count / elapsed
    recv_rate = recv_count / elapsed
    drop_pct  = (1 - recv_count / max(sent_count, 1)) * 100
    send_mbps = sent_bytes / elapsed / 1e6
    recv_mbps = recv_bytes / elapsed / 1e6

    print(f"  Sent:     {sent_count:,} msgs  ({send_rate:,.0f} msg/s  {send_mbps:.2f} MB/s)")
    print(f"  Received: {recv_count:,} msgs  ({recv_rate:,.0f} msg/s  {recv_mbps:.2f} MB/s)")
    print(f"  Drop:     {drop_pct:.1f}%")
    print()
    return sent_count, recv_count, elapsed


# ---------------------------------------------------------------------------
# TEST 4 - Scale Test
# ---------------------------------------------------------------------------
async def test_scale(server: str, steps: Optional[List[int]] = None, msg_count: int = 200, tick_hz: float = 20.0):
    if steps is None:
        steps = SCALE_STEPS
    tick_interval = 1.0 / tick_hz
    separator()
    print(f"TEST 4 - Scale Test  (spectator counts: {steps}, send rate: {tick_hz:.0f} Hz)")
    separator()
    print(f"  {'Spectators':>10}  {'p50':>9}  {'p95':>9}  {'p99':>9}  {'recv/sent':>10}  {'msg/s':>8}")
    print(f"  {'-'*10}  {'-'*9}  {'-'*9}  {'-'*9}  {'-'*10}  {'-'*8}")

    results = []

    for spec_count in steps:
        room   = f"bench-scale-{uuid.uuid4().hex[:6]}"
        master = await connect(server, room)
        await asyncio.wait_for(master.recv(), timeout=2)

        specs = []
        for _ in range(spec_count):
            s = await connect(server, room)
            try:
                await asyncio.wait_for(s.recv(), timeout=2)
            except Exception:
                pass
            specs.append(s)

        # Drain any snapshot/request messages
        await asyncio.sleep(0.3)
        for s in specs:
            while True:
                try:
                    await asyncio.wait_for(s.recv(), timeout=0.05)
                except asyncio.TimeoutError:
                    break

        samples     = []
        total_recv  = 0
        total_sent  = 0

        async def recv_all():
            nonlocal total_recv
            tasks = []
            for s in specs:
                tasks.append(_recv_samples(s, msg_count, samples))
            results_list = await asyncio.gather(*tasks, return_exceptions=True)
            total_recv = sum(r for r in results_list if isinstance(r, int))

        t0 = time.perf_counter()
        recv_task = asyncio.create_task(recv_all())

        # Send at a realistic game tick rate rather than flooding.
        # Flooding fills channel queues and gives artificially high latency.
        next_send = time.perf_counter()
        for _ in range(msg_count):
            now = time.perf_counter()
            if next_send > now:
                await asyncio.sleep(next_send - now)
            await master.send(make_event(0, time.perf_counter_ns()))
            total_sent += 1
            next_send += tick_interval

        await recv_task
        elapsed = time.perf_counter() - t0

        await master.close()
        for s in specs:
            await s.close()

        if samples:
            p50, _, p95, p99 = percentiles(samples)
            rate = len(samples) / elapsed
            ratio = f"{total_recv}/{total_sent * spec_count}"
            print(f"  {spec_count:>10}  {fmt_ms(p50):>9}  {fmt_ms(p95):>9}  {fmt_ms(p99):>9}  {ratio:>10}  {rate:>7.0f}/s")
            results.append((spec_count, p50, p95, p99, total_recv, total_sent * spec_count))
        else:
            print(f"  {spec_count:>10}  {'no data':>9}")

    print()
    return results


async def _recv_samples(ws, expected: int, out_samples: list) -> int:
    count = 0
    while count < expected:
        try:
            raw = await asyncio.wait_for(ws.recv(), timeout=15.0)
            if isinstance(raw, bytes):
                ts = extract_event_ts(raw)
                if ts is not None:
                    out_samples.append(time.perf_counter_ns() - ts)
                    count += 1
        except (asyncio.TimeoutError, ConnectionClosed):
            break
    return count


# ---------------------------------------------------------------------------
# TEST 5 - Connection Storm
# ---------------------------------------------------------------------------
async def test_connection_storm(server: str, client_count: int = STORM_CLIENTS):
    separator()
    print(f"TEST 5 - Connection Storm  ({client_count} clients connecting simultaneously)")
    separator()

    room = f"bench-storm-{uuid.uuid4().hex[:6]}"
    connect_times = []
    errors = 0

    async def one_connect():
        nonlocal errors
        t0 = time.perf_counter_ns()
        try:
            ws = await asyncio.wait_for(connect(server, room), timeout=10.0)
            await asyncio.wait_for(ws.recv(), timeout=5.0)  # wait for role assignment
            connect_times.append(time.perf_counter_ns() - t0)
            return ws
        except Exception as e:
            errors += 1
            return None

    t_wall = time.perf_counter()
    conns  = await asyncio.gather(*[one_connect() for _ in range(client_count)])
    total  = time.perf_counter() - t_wall

    active = [c for c in conns if c is not None]

    print(f"  Connected:  {len(active)}/{client_count}  ({errors} errors)")
    print(f"  Wall time:  {total*1000:.1f} ms  for all {client_count} connections")
    if connect_times:
        print_latency_table("Per-client connect+role-assign latency", connect_times)

    # Clean up
    for ws in active:
        try:
            await ws.close()
        except Exception:
            pass

    print()
    return connect_times


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
async def main():
    parser = argparse.ArgumentParser(description="Somnium Network Benchmark Suite")
    parser.add_argument("--server",     default=DEFAULT_SERVER, help="WebSocket server URL")
    parser.add_argument("--test",       default="all",  choices=["all","ping","relay","throughput","scale","storm"])
    parser.add_argument("--scale-max",  type=int, default=50,   help="Max spectators for scale test")
    parser.add_argument("--duration",   type=float, default=THROUGHPUT_SECS, help="Throughput test duration (secs)")
    parser.add_argument("--storm",      type=int, default=STORM_CLIENTS,     help="Clients for connection storm")
    parser.add_argument("--ping-count", type=int, default=PING_COUNT)
    parser.add_argument("--relay-count",type=int, default=RELAY_COUNT)
    args = parser.parse_args()

    scale_steps = []
    n = args.scale_max
    for step in [1, 5, 10, 25, 50, 100, 200]:
        if step <= n:
            scale_steps.append(step)
    if n not in scale_steps:
        scale_steps.append(n)

    print()
    print("=" * 60)
    print(f"  Somnium Network Benchmark - {args.server}")
    print("=" * 60)
    print()

    run_all = args.test == "all"

    if run_all or args.test == "ping":
        await test_ping(args.server, args.ping_count)

    if run_all or args.test == "relay":
        await test_relay_latency(args.server, args.relay_count)

    if run_all or args.test == "throughput":
        await test_throughput(args.server, args.duration)

    if run_all or args.test == "scale":
        await test_scale(args.server, scale_steps)

    if run_all or args.test == "storm":
        await test_connection_storm(args.server, args.storm)

    separator("=", 60)
    print("  Done.")
    separator("=", 60)
    print()


if __name__ == "__main__":
    asyncio.run(main())

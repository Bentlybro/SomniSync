"""Exercise a real relay with thousands of simulated scene bodies.

Requires the `websockets` Python package. Run against a disposable relay room.
This is a measurement tool: it connects as clients, uploads a generated scene,
and counts bytes actually received over each WebSocket.
"""

import argparse
import asyncio
import json
import math
import statistics
import struct
import time
import urllib.request

import websockets


OBJECT = struct.Struct("<HBB3f3f3f5f")


def scene_init(count: int) -> bytes:
    width = math.ceil(math.sqrt(count))
    payload = bytearray(b"\x10" + struct.pack("<H", count))
    for object_id in range(count):
        x = (object_id % width) * 2.5
        z = (object_id // width) * 2.5
        payload.extend(
            OBJECT.pack(
                object_id, 0, 0,  # dynamic sphere
                x, 20.0, z,        # position
                0.0, 0.0, 0.0,     # rotation
                0.3, 0.0, 0.0,     # sphere radius
                1.0, 0.0, 0.5, 0.0, 0.0,
            )
        )
    return bytes(payload)


async def receive_for(socket, seconds: float) -> dict:
    end = time.monotonic() + seconds
    frames = bytes_received = entities = compact_frames = 0
    rtts = []
    while time.monotonic() < end:
        try:
            frame = await asyncio.wait_for(socket.recv(), min(0.5, end - time.monotonic()))
        except asyncio.TimeoutError:
            continue
        if not isinstance(frame, bytes):
            continue
        frames += 1
        bytes_received += len(frame)
        if frame and frame[0] == 0x0F:
            compact_frames += 1
            if len(frame) >= 7:
                entities += struct.unpack_from("<H", frame, 5)[0]
        elif frame[:2] == b"\xff\x0e" and len(frame) >= 10:
            sent_ms = struct.unpack_from("<Q", frame, 2)[0]
            rtts.append(time.monotonic_ns() / 1_000_000 - sent_ms)
    return {
        "frames": frames,
        "bytes": bytes_received,
        "bytes_per_second": round(bytes_received / seconds),
        "compact_frames": compact_frames,
        "compact_entities": entities,
        "rtt_ms_p50": round(statistics.median(rtts), 1) if rtts else None,
        "rtt_ms_max": round(max(rtts), 1) if rtts else None,
    }


async def send_pings(socket, seconds: float):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        await socket.send(b"\x0e" + struct.pack("<Q", time.monotonic_ns() // 1_000_000))
        await asyncio.sleep(0.5)


def metrics(url: str) -> dict:
    with urllib.request.urlopen(url, timeout=3) as response:
        data = json.load(response)
    return {
        key: data.get(key)
        for key in (
            "cpu_percent", "rss_bytes", "physics_tick_p95_us",
            "physics_broadcast_p95_us", "total_msgs_dropped",
        )
    }


async def run(args):
    if not 1 <= args.objects <= 16384:
        raise ValueError("--objects must be between 1 and 16384")
    if not 1 <= args.clients <= 64:
        raise ValueError("--clients must be between 1 and 64")

    room_url = args.url.rstrip("/") + "/load-" + str(time.time_ns()) + f"?scene={args.objects:016x}"
    sockets = []
    try:
        for _ in range(args.clients):
            socket = await websockets.connect(room_url, max_size=None, compression=None)
            sockets.append(socket)
            welcome = await asyncio.wait_for(socket.recv(), 5)
            if not isinstance(welcome, bytes) or welcome[:2] != b"\xff\x01":
                raise RuntimeError("relay did not send WELCOME")
            await socket.send(b"\xff\x02\x01")  # compact positions
            if args.aoi:
                await socket.send(struct.pack("<B5f", 0x0C, -5.0, 20.0, -5.0, 0.3, 0.9))
        scene = scene_init(args.objects)
        await sockets[0].send(scene)
        # Drain WELCOME/PHYSICS_READY and the first second of state so the
        # timed window does not count frames buffered during warmup.
        await asyncio.gather(*(receive_for(socket, 1.0) for socket in sockets))
        pingers = [asyncio.create_task(send_pings(socket, args.seconds)) for socket in sockets]
        results = await asyncio.gather(*(receive_for(socket, args.seconds) for socket in sockets))
        await asyncio.gather(*pingers)
        print(json.dumps({
            "objects": args.objects,
            "clients": args.clients,
            "aoi": args.aoi,
            "scene_init_bytes": len(scene),
            "duration_seconds": args.seconds,
            "clients_received": results,
            "server": metrics(args.metrics) if args.metrics else None,
        }, indent=2))
    finally:
        await asyncio.gather(*(socket.close() for socket in sockets), return_exceptions=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="ws://127.0.0.1:9090")
    parser.add_argument("--metrics", help="Optional private relay metrics URL, e.g. http://127.0.0.1:9091/metrics")
    parser.add_argument("--objects", type=int, default=4000)
    parser.add_argument("--clients", type=int, default=1)
    parser.add_argument("--aoi", action="store_true", help="Report a player position so AOI filtering applies")
    parser.add_argument("--seconds", type=float, default=5)
    asyncio.run(run(parser.parse_args()))
